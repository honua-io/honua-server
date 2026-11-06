// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security.Admission;

internal sealed partial class RedisAiExecutionAdmissionStore
{
    // No keys or fields expire independently. Eviction removes this whole authority, which denies
    // until explicit stopped bootstrap and its 3630-second quarantine. Scripts never auto-initialize.
    private const string Script = """
        local key, op = KEYS[1], ARGV[1]
        local clock = redis.call('TIME')
        local now = tonumber(clock[1])*1000 + math.floor(tonumber(clock[2])/1000)
        local function reply(code, state, reservation, applied)
            return cjson.encode({code=code, state=state, reservation=reservation, applied=applied or false})
        end
        local function integer(value, low, high)
            return type(value)=='number' and value==math.floor(value) and value>=low and value<=high
        end
        local function digest(value)
            return type(value)=='string' and #value==64 and not string.find(value, '[^0-9a-f]')
        end
        local function uuid(value)
            return type(value)=='string' and #value==32 and not string.find(value, '[^0-9a-f]') and value~=string.rep('0',32)
        end
        local function decode(value)
            if not value or #value>16384 then return nil end
            local ok, parsed=pcall(cjson.decode,value)
            if ok and type(parsed)=='table' then return parsed end
            return nil
        end
        local function policy_valid(p)
            if type(p)~='table' or not integer(p.windowSeconds,1,3600) or not integer(p.maxOperationSeconds,1,3600) then return false end
            for _, field in ipairs({'deploymentAttempts','actorAttempts','cohortAttempts','workflowAttempts',
                'deploymentConcurrency','actorConcurrency','cohortConcurrency','workflowConcurrency'}) do
                if not integer(p[field],1,8192) then return false end
            end
            return true
        end
        local function state_valid(s)
            return type(s)=='table' and s.schema==1 and uuid(s.generation) and policy_valid(s.policy)
                and integer(s.revision,1,9007199254740000) and integer(s.epoch,1,9007199254740000)
                and type(s.stopped)=='boolean' and integer(s.notBefore,0,253402300000000)
        end
        -- Bind to the owner actually inspected by this invocation. CONFIG is noscript, so C#
        -- observes strict settings before and after; server identity and live AOF health are
        -- additionally checked here before any mutation on the executing Redis process.
        local serverInfo=redis.call('INFO','server')
        local replicationInfo=redis.call('INFO','replication')
        local persistenceInfo=redis.call('INFO','persistence')
        local function info_value(info, name)
            return string.match(info, '\r\n'..name..':([^\r\n]+)')
        end
        if info_value(serverInfo,'run_id')~=ARGV[#ARGV-1]
            or info_value(replicationInfo,'master_replid')~=ARGV[#ARGV]
            or info_value(replicationInfo,'role')~='master'
            or info_value(persistenceInfo,'aof_enabled')~='1'
            or info_value(persistenceInfo,'aof_last_write_status')~='ok' then return reply('unavailable') end
        local ttl=redis.call('PTTL',key)
        if ttl~=-1 and ttl~=-2 then return reply('invalid_policy') end
        if redis.call('HLEN',key)>9217 then return reply('invalid_policy') end
        local function read(field)
            if redis.call('HSTRLEN',key,field)>16384 then return nil end
            return redis.call('HGET',key,field)
        end
        if redis.call('HSTRLEN',key,'state')>16384 then return reply('invalid_policy') end
        local raw=read('state')
        local state=decode(raw)
        if raw and not state_valid(state) then return reply('invalid_policy') end
        if not state and redis.call('EXISTS',key)==1 then return reply('invalid_policy') end

        -- Every stored attempt is validated before a debit or capacity-sensitive control. The fixed
        -- supported history horizon survives later window enlargement; live leases are never pruned.
        local function attempts()
            local rows, expired={},{}
            local controlCount,attemptCount=0,0
            for _, field in ipairs(redis.call('HKEYS',key)) do
                if string.sub(field,1,2)=='a:' then
                    attemptCount=attemptCount+1
                    if attemptCount>8192 or #field~=34 or not uuid(string.sub(field,3)) then return nil end
                    local a=decode(read(field))
                    if not a or a.generation~=state.generation or not integer(a.epoch,1,state.epoch)
                        or not digest(a.actor) or (a.useCase~='studio-chat' and a.useCase~='reporting-narrative' and a.useCase~='imagery-inference')
                        or (a.workflow~='' and not digest(a.workflow)) or not integer(a.admitted,0,9007199254740000)
                        or not integer(a.deadline,a.admitted+1000,a.admitted+3600000) or a.lease~=a.deadline+30000
                        or type(a.released)~='boolean' then return nil end
                    if a.admitted+3600000<=now and (a.released or a.lease<=now) then
                        table.insert(expired,field)
                    else
                        table.insert(rows,a)
                    end
                elseif string.sub(field,1,2)=='c:' then
                    controlCount=controlCount+1
                    if #field~=66 or not digest(string.sub(field,3)) or controlCount>1024 then return nil end
                elseif field~='state' then return nil end
            end
            if #rows>8192 then return nil end
            return rows,expired
        end
        local function prune(fields)
            for _, field in ipairs(fields) do redis.call('HDEL',key,field) end
        end
        if op=='state' then
            if not state then return reply('uninitialized') end
            return reply('state',state)
        end
        if op=='current' or op=='release' then
            if not state then return reply('uninitialized') end
            if ARGV[3]~=state.generation then return reply('stale_reservation') end
            local field='a:'..ARGV[2]
            local a=decode(read(field))
            if not a or a.generation~=ARGV[3] or a.epoch~=tonumber(ARGV[4])
                or a.deadline~=tonumber(ARGV[5]) or a.lease~=tonumber(ARGV[6]) then return reply('stale_reservation') end
            if op=='release' then
                if not a.released then a.released=true; redis.call('HSET',key,field,cjson.encode(a)) end
                return reply('released')
            end
            local rows=attempts()
            if not rows then return reply('invalid_policy') end
            if state.stopped or state.epoch~=a.epoch or a.released or now>=a.deadline or now<state.notBefore then
                return reply('stale_reservation')
            end
            return reply('current')
        end
        if op=='acquire' then
            if not state then return reply('uninitialized') end
            if state.stopped then return reply('stopped') end
            if now<state.notBefore then return reply('recovering') end
            local field='a:'..ARGV[2]
            if redis.call('HEXISTS',key,field)==1 then return reply('duplicate_attempt') end
            local rows,expired=attempts()
            if not rows then return reply('invalid_policy') end
            if #rows>=8192 then return reply('ledger_full') end
            local used={0,0,0,0}
            local active={0,0,0,0}
            for _, a in ipairs(rows) do
                local scopes={true,a.actor==ARGV[3],a.actor==ARGV[3] and a.useCase==ARGV[4],ARGV[5]~='' and a.workflow==ARGV[5]}
                for i=1,4 do
                    if scopes[i] then
                        if a.admitted>now-state.policy.windowSeconds*1000 then used[i]=used[i]+1 end
                        if not a.released and a.lease>now then active[i]=active[i]+1 end
                    end
                end
            end
            local limits={state.policy.deploymentAttempts,state.policy.actorAttempts,state.policy.cohortAttempts,state.policy.workflowAttempts}
            local slots={state.policy.deploymentConcurrency,state.policy.actorConcurrency,state.policy.cohortConcurrency,state.policy.workflowConcurrency}
            for i=1,(ARGV[5]=='' and 3 or 4) do
                if used[i]>=limits[i] then return reply('budget_exceeded') end
                if active[i]>=slots[i] then return reply('concurrency_exceeded') end
            end
            local deadline=now+state.policy.maxOperationSeconds*1000
            local a={generation=state.generation,epoch=state.epoch,actor=ARGV[3],useCase=ARGV[4],workflow=ARGV[5],
                admitted=now,deadline=deadline,lease=deadline+30000,released=false}
            prune(expired)
            redis.call('HSET',key,field,cjson.encode(a))
            return reply('admitted',nil,{generation=a.generation,epoch=a.epoch,deadline=a.deadline,lease=a.lease})
        end
        if op~='control' then return reply('invalid_control') end
        local action, expectedGeneration=ARGV[2],ARGV[3]
        local expectedRevision, expectedEpoch=tonumber(ARGV[4]),tonumber(ARGV[5])
        local actor,invocation,correlation=ARGV[6],ARGV[7],ARGV[8]
        if #actor<1 or #actor>4096 or #invocation<1 or #invocation>128 or #correlation<1 or #correlation>128 then
            return reply('invalid_control')
        end
        local policy=decode(ARGV[9])
        local generation,fingerprint,eventKey=ARGV[10],ARGV[11],'c:'..ARGV[12]
        local events,expired={},{}
        for _, field in ipairs(redis.call('HKEYS',key)) do
            if string.sub(field,1,2)=='c:' then
                if #field~=66 or not digest(string.sub(field,3)) then return reply('invalid_policy') end
                local event=decode(read(field))
                if not event or not integer(event.at,0,9007199254740000) or not digest(event.fingerprint)
                    or not state_valid(event.state) or event.state.generation~=state.generation
                    or (event.code~='initialized' and event.code~='updated' and event.code~='stopped' and event.code~='resumed')
                    or type(event.actor)~='string' or #event.actor>4096
                    or type(event.invocation)~='string' or #event.invocation>128
                    or type(event.correlation)~='string' or #event.correlation>128 then return reply('invalid_policy') end
                -- Replay before retention cleanup: never execute a second mutation for this invocation.
                if field==eventKey then
                    if event.fingerprint~=fingerprint then return reply('invalid_control',state) end
                    return reply(event.code,event.state,nil,true)
                end
                if event.at+2592000000<=now then table.insert(expired,field) else table.insert(events,event) end
            elseif field~='state' and string.sub(field,1,2)~='a:' then return reply('invalid_policy') end
        end
        if #events+#expired>1024 then return reply('invalid_policy') end
        local code
        if action=='Initialize' then
            if state then return reply('already_initialized',state) end
            if not policy_valid(policy) or not uuid(generation) then return reply('invalid_control') end
            state={schema=1,generation=generation,policy=policy,revision=1,epoch=1,stopped=true,notBefore=now+3630000}
            code='initialized'
        else
            if not state then return reply('uninitialized') end
            if expectedGeneration~=state.generation or expectedRevision~=state.revision or expectedEpoch~=state.epoch then
                return reply('stale_control',state)
            end
            if action=='Stop' and state.stopped then return reply('stopped',state,nil,false) end
            if action=='Resume' and not state.stopped then return reply('resumed',state,nil,false) end
            if action=='Resume' and now<state.notBefore then return reply('recovering',state) end
            if action=='UpdatePolicy' then
                if not state.stopped or not policy_valid(policy) then return reply('invalid_control',state) end
                local rows,garbage=attempts()
                if not rows then return reply('invalid_policy') end
                for _, a in ipairs(rows) do
                    if not a.released and a.lease>now then return reply('policy_not_drained',state) end
                end
                state.policy=policy
                state.revision=state.revision+1
                code='updated'
            elseif action=='Stop' then state.stopped=true; code='stopped'
            elseif action=='Resume' then state.stopped=false; code='resumed'
            else return reply('invalid_control',state) end
            state.epoch=state.epoch+1
        end
        -- Reserve the final durable audit slot for STOP. No-op/rejected controls above consume none.
        if #events>=(action=='Stop' and 1024 or 1023) then return reply('audit_full') end
        local event={at=now,actor=actor,invocation=invocation,correlation=correlation,action=action,
            fingerprint=fingerprint,code=code,state=state}
        local encodedState,encodedEvent=cjson.encode(state),cjson.encode(event)
        if #encodedState>16384 or #encodedEvent>16384 then return reply('invalid_control') end
        -- All writes happen after every validation; a refusal cannot partially update policy/epoch.
        prune(expired)
        redis.call('HSET',key,'state',encodedState,eventKey,encodedEvent)
        return reply(code,state,nil,true)
        """;
}
