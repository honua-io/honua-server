# MVP SaaS-ish Launch GTM Plan

## Purpose
This playbook defines a low-touch, product-led launch motion with simple pricing, self-service trials, and cloud marketplace distribution.

## Launch Goals (First 90 Days)
- `100-200` Community edition deployments
- `20-40` Community-to-Pro conversions
- `5-10` Pro-to-Enterprise upgrades
- `$25k-$50k` MRR across paid tiers
- `5-8` case studies from successful implementations

## Target Market Segmentation

Keep targeting broad but focused on self-service adoption patterns.

### Primary Segments (Product-Led Growth)

**Developer-First Organizations**
- Teams building location-aware applications
- Need multi-protocol geospatial APIs quickly
- Value: Deploy in minutes vs. weeks, avoid vendor lock-in

**Digital Transformation Teams**
- Modernizing legacy GIS infrastructure
- Want cloud-native, API-first architecture
- Value: Reduce ArcServer licensing costs, improve performance

**SaaS Platform Builders**
- Need to add geospatial capabilities to existing products
- Require flexible, scalable API infrastructure
- Value: Fast time-to-market, protocol flexibility

## Core Offer Design
Simple SaaS pricing with self-service trials and marketplace distribution.

- **Free trial**: 30-day trial with full features, deployment support
- **Monthly subscriptions**: No annual commitments, usage-based scaling
- **Self-service deployment**: Terraform + docs, minimal sales involvement
- **Value messaging**: Deploy faster, cost less, avoid vendor lock-in

## Pilot Timeline and Clock Rules
- `Day 0-7` (pre-pilot): Stages 1-4 (triage, qualification, technical discovery, proposal)
- Pilot clock starts only after:
  - Signed SOW/order form
  - Named executive sponsor and implementation owner
  - Kickoff date confirmed
- Pilot delivery window (`6-8 weeks`) covers Stages 5-6:
  - Default delivery model: `6 weeks` execution + readout/conversion in `Week 7`
  - Optional extension to `Week 8` requires written scope-change approval

## Qualification Framework (Go/No-Go)
Use explicit criteria to prevent low-probability pilots from consuming delivery capacity.

### Required to Proceed (all must be true)
- Business pain is clear and tied to an active project in the next `90 days`
- Internal champion is identified and attends discovery
- Technical owner is identified and available weekly during pilot
- Buying path is clear (budget source + decision stakeholders + expected approval path)
- Pilot success metrics are measurable against a baseline in `6-8 weeks`

### Disqualifiers (stop or nurture)
- "Just exploring" with no active timeline or owner
- No data access path in pilot timeline
- No decision-maker access for conversion discussion
- Security/procurement process cannot start during Week 1
- Requested scope exceeds fixed pilot format and cannot be reduced

## Go-to-Market Motion (Product-Led)
1. **Cloud Marketplace Launch**: AWS + Azure marketplace listings with one-click deploy
2. **Content Marketing**: Technical blogs, deployment guides, comparison content
3. **Developer Community**: GitHub presence, documentation site, example projects
4. **Self-Service Trials**: 30-day free trials with automated onboarding
5. **Low-Touch Sales**: Founder involvement only for Enterprise tier and custom needs
6. **Customer Success**: Focus on trial-to-paid conversion and usage expansion

## Customer Acquisition Channels

**Primary: Self-Service Trial**
- Landing page CTA: `Start Free Trial`
- Instant trial environment provisioning
- Guided onboarding with sample data
- In-app upgrade prompts after trial value realization

**Secondary: Cloud Marketplace**
- AWS/Azure marketplace discovery
- One-click subscription and deployment
- Integrated billing through cloud provider
- Marketplace co-marketing opportunities

### Intake Form Structure
Use `2` steps to balance completion rate and qualification quality.

- Step 1 (fast):
  - Name, company, role, work email
  - Primary use case / ICP segment
- Step 2 (qualification):
  - Timeline and urgency
  - Data volume/complexity
  - Technical implementation owner
  - Decision stakeholders and budget path
  - Security/procurement timeline

### Auto-Routing Logic with Pilot Tier Assignment
After submit, auto-score and route:

**Go + Pilot Tier Assignment:**
- **Simple Pilot Route**: Single protocol, <1M features, municipal/POC → Book qualification call
- **Standard Pilot Route**: Multi-protocol, 1-10M features, utility/SaaS → Book discovery call
- **Complex Pilot Route**: Full suite, >10M features, enterprise → Book technical assessment
- Create CRM record in appropriate `Qualified-[Tier]` stage
- Assign owner based on pilot complexity and start response SLA timer

**Nurture Route:**
- Good ICP fit but timing >6 months → Nurture sequence + quarterly re-qualification
- Budget unclear but technical fit strong → Educational content + follow-up in 30 days

**No-fit Route:**
- Out of ICP segments → Polite deferral with self-serve resources
- No technical fit → Close as `Not ICP` with specific reason code

### Pilot Tier Qualification Criteria

| Criteria | Simple | Standard | Complex |
| --- | --- | --- | --- |
| **Protocols needed** | 1 (REST or OGC) | 2-3 (REST+OGC+MVT) | Full suite + custom |
| **Data volume** | <1M features | 1-10M features | >10M features |
| **Timeline urgency** | 3-6 months | 1-3 months | <1 month |
| **Budget authority** | <$25k | $25k-$100k | >$100k |
| **Technical complexity** | Standard migration | Multi-system integration | Custom federation |

## Customer Journey by Segment

### Lower Tier (Marketplace-First)
1. **Discovery**: Find Honua on AWS/Azure marketplace
2. **Trial**: One-click trial deployment via marketplace
3. **Value Realization**: Import data, test APIs, measure performance
4. **Conversion**: Upgrade to paid tier through marketplace billing

### Mid Tier (Self-Service)
1. **Discovery**: Find Honua via content, GitHub, or search
2. **Trial**: Download Terraform templates, deploy in own account
3. **Support**: Use documentation, community forums, GitHub issues
4. **Conversion**: Subscribe via website, continue using own deployment

### Enterprise (Professional Services)
1. **Discovery**: Referral, content marketing, or direct outreach
2. **Evaluation**: Technical consultation + architecture review
3. **Custom Deployment**: Professional services engagement
4. **Subscription**: Enterprise tier with ongoing support relationship

## Unified Technical + Sales Architecture
Design this as one connected system: a single lead/deal/pilot lifecycle with clear owners, stage exit criteria, and automation triggers.

### Core System Components
| Layer | Purpose | MVP Recommendation | Upgrade Trigger |
| --- | --- | --- | --- |
| Web + CTA | Capture inbound demand | GitHub site + form CTA (`Start Pilot Assessment`) | Upgrade when you need A/B testing, dynamic landing pages |
| Intake + scoring | Collect qualification data and route lead status | Typeform/Tally + scoring rules (`Go/Nurture/No-fit`) | Upgrade when you need custom scoring logic/modeling |
| Workflow orchestration | Run event automation across tools | n8n (self-hosted) or Zapier | Upgrade when workflow volume/logic gets complex |
| CRM pipeline | Source of truth for account/deal stages | HubSpot CRM (Starter) | Upgrade when multi-team routing and forecasting depth are required |
| Scheduling | Remove friction from qualified lead booking | Calendly (router + owner round-robin) | Upgrade when complex territory routing is needed |
| Proposal + signature | Send SOW/order form and capture execution | PandaDoc or DocuSign | Upgrade when legal workflow requires CLM |
| Billing + subscription | Collect pilot payment, convert to annual | Stripe (payment links + subscriptions) | Upgrade when invoicing/procurement complexity increases |
| Pilot provisioning | Convert signed deal to active pilot tenant/setup | Honua admin/provisioning API + internal runbook | Upgrade when provisioning needs full workflow engine |
| Support + execution | Track pilot work, bugs, and risk register | Linear/Jira + shared Slack channel | Upgrade when support requires formal ITSM |
| KPI dashboard | Weekly operating metrics and funnel health | HubSpot dashboard + simple warehouse/BI later | Upgrade when board-level forecasting is needed |

### Sales Workflow (Human Process + Exit Criteria)
| Stage | Owner | Objective | Exit Criteria |
| --- | --- | --- | --- |
| Inbound triage | SDR/Founder | Validate ICP and urgency | `Go/Nurture/No-fit` decided in `24h` |
| Qualification call | Founder/AE | Confirm pain, champion, budget path | Proceed criteria all true |
| Technical discovery | Solutions + Eng | Validate fit, risks, and success baseline | Baseline metric + implementation path signed off |
| Proposal + deal desk | Founder + Legal/Ops | Lock scope, timeline, price, terms | SOW sent with target signature date |
| Pilot close | Buyer + Founder | Complete commercial + legal execution | Signed agreement + kickoff date |
| Pilot delivery | Eng + CSM | Execute scoped outcomes with weekly cadence | Readout-ready outcomes and metrics captured |
| Executive readout | Founder + Champion | Prove impact and present annual plan | Conversion decision date committed |
| Conversion/expansion | Founder + Buyer | Move from pilot to annual agreement | Annual signed or time-bound mutual action plan |

### Technical Workflow (System Automations)
1. `LeadSubmitted` event: form submission creates/updates contact, account, and opportunity in CRM.
2. `LeadScored` event: scoring logic sets `Go/Nurture/No-fit` and auto-assigns owner.
3. `GoQualified` event: system sends booking link + owner Slack alert + SLA timer.
4. `CallBooked` event: CRM stage advances to `Qualification Scheduled`; prep template auto-generated.
5. `DiscoveryCompleted` event: technical checklist and baseline metric fields are required before proposal stage unlocks.
6. `ProposalSent` event: signature packet link, legal checklist, and expected close date are written to CRM.
7. `ProposalSigned` event: billing trigger fires, onboarding ticket is created, kickoff workflow starts.
8. `PilotStarted` event: pilot workspace/channel created, weekly status cadence and health score tracking starts.
9. `PilotReadoutReady` event: KPI delta report template auto-populates from baseline and outcome fields.
10. `ConversionWon/Lost` event: create annual account plan or loss-reason + nurture re-entry workflow.

### Canonical Data Model (Must Exist in CRM)
Standardize these records so sales and delivery stay synced:
- `Account`: segment, ARR potential, procurement profile, security requirements
- `Contact`: role (`champion`, `economic buyer`, `technical owner`), influence level
- `Opportunity`: stage, amount, close date, confidence, next action/date
- `Pilot`: start date, end date, success metrics baseline, support hours budget, risk status
- `Outcome`: before/after KPI values, conversion recommendation, case-study status

### SLA + Governance Rules
- Inbound response SLA: `<=24h` on business days
- Stage aging alerts:
  - Qualification not booked in `3 days`
  - Proposal unsigned after `10 business days`
  - No weekly update logged during active pilot
- Mandatory fields before stage advance:
  - No move to proposal without success baseline
  - No move to kickoff without signed scope + payment trigger
  - No move to conversion without readout metrics completed

### MVP Build Order (Execution Plan)
1. Week 1: publish form-first CTA, CRM stages, and lead scoring/routing.
2. Week 2: connect scheduling, proposal e-sign, and SLA alerts.
3. Week 3: connect signed-deal to pilot onboarding workflow and support tracking.
4. Week 4: automate readout template + conversion workflow + weekly KPI dashboard.

## CTA to Pilot Engagement Process
### Stage 1: Inbound Triage (`Day 0-1`)
- Respond within `24 hours`
- Collect initial qualification details:
  - Primary use case
  - Data volume and complexity
  - Timeline and implementation owner
- Exit criteria:
  - `Go`: prospect fits at least one launch ICP and has an owner + timeline
  - `No-go`: out-of-ICP or no accountable owner/timeline

### Stage 2: Qualification Call (`Day 2-4`)
- Run a `30-minute` qualification call
- Confirm:
  - Clear problem and urgency
  - Internal champion
  - Budget path and decision stakeholders
  - Pilot-fit use case
- Exit criteria:
  - `Go`: all required "Proceed" criteria are met
  - `No-go`: missing champion, budget path, or measurable pilot outcome

### Stage 3: Technical Discovery (`Week 1`)
- Run a `60-minute` technical deep dive
- Capture environment constraints, integration requirements, and risks
- Define measurable success criteria and baseline metrics
- Exit criteria:
  - `Go`: data access, integration path, and success baseline are confirmed
  - `No-go`: unresolved blocking technical constraints with no mitigation plan

### Stage 4: Pilot Proposal (`End of Week 1`)
- Send pilot SOW with:
  - Scope and deliverables
  - Timeline and milestones
  - Support model and SLA
  - Pilot fee and conversion terms
  - Case-study/anonymized-results clause
- Include procurement packet:
  - Security questionnaire response packet
  - DPA/MSA redlines ownership
  - Buyer/legal timeline and target signature date
- Exit criteria:
  - Signed pilot agreement and kickoff date locked

### Stage 5: Kickoff and Delivery (`Weeks 2-7`)
- Kickoff call with technical + business stakeholders
- Weekly check-ins with issue/risk tracking
- Track progress against predefined success metrics
- Scope control:
  - Any work outside SOW requires written change order approval
  - Forecasted overrun beyond agreed hours triggers re-scope or paid add-on

### Stage 6: Readout and Conversion (`Week 8`)
- Deliver executive readout:
  - Baseline vs outcome metrics (see success criteria below)
  - Operational impact assessment
  - Production rollout recommendation and roadmap
- Present annual conversion offer with commercial terms
- Exit criteria:
  - Conversion decision date committed
  - If no immediate conversion, agree time-bound mutual action plan

### Pilot Success Criteria by Tier

**Simple Pilot Success Metrics:**
- API response time improvement: >30%
- Data accessibility improvement: 100% of target datasets available via API
- Implementation time: <2 weeks from data access to live endpoint

**Standard Pilot Success Metrics:**
- Multi-protocol performance: All 3 protocols (REST/OGC/MVT) operational
- Query performance: >50% improvement over baseline on complex spatial queries
- Integration effort reduction: >40% fewer development hours for client integrations

**Complex Pilot Success Metrics:**
- Federation performance: >60% faster cross-system data aggregation
- Protocol flexibility: Support for client-specific protocol variations
- Operational efficiency: >50% reduction in data pipeline maintenance overhead

### Stage 7: Case Study Capture (`Post-Pilot`)
- Draft case study within `7 days` of pilot completion
- Publish named case study where permitted; otherwise publish anonymized impact summary

## Case Study Strategy (Launch Priority)
- Select `2` lighthouse pilot customers with high-clarity before/after outcomes
- Lock case-study approval language in pilot contract upfront
- Collect baseline metrics during kickoff; collect endline metrics during close
- Keep final case study short and proof-heavy:
  - Problem context
  - Implementation approach
  - Quantified outcomes
  - Customer quote

## Support Model for Initial Launch
### Included in Standard Pilot
- Async support (email/slack): up to `12 hours` total across pilot
- Weekly working session: up to `6` sessions (`60 minutes` each)
- Basic implementation guidance within agreed pilot scope
- Issue response SLA:
  - `P1` (production-blocking pilot issue): first response within `4 business hours`, status update at least every `1 business day`
  - `P2` (high impact, workaround exists): first response by next business day
  - `P3` (standard requests/questions): first response within `2 business days`
- Overage policy:
  - Hours beyond included scope billed at blended delivery rate (`$100-$130/hour`) or premium add-on package

### Optional Premium Support Add-On
- Faster SLA / priority handling
- Shared channel with tighter response windows
- Extra architecture/enablement sessions
- Suggested pricing: `+$2k-$5k/month`

## Support Cost Model (Per Pilot Tier)
Assumptions:
- Blended internal delivery rate: `$120/hour`
- Infrastructure/tools: `$500` per pilot average

| Pilot Type | Total Hours | Internal Cost | Infrastructure | **Total Cost** | **Revenue** | **Margin** |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Simple | `20h` | `$2,400` | `$500` | **`$2,900`** | **`$8,000`** | **`64%`** |
| Standard | `30h` | `$3,600` | `$500` | **`$4,100`** | **`$12,000`** | **`66%`** |
| Complex | `45h` | `$5,400` | `$500` | **`$5,900`** | **`$18,000`** | **`67%`** |

### Hour Allocation by Pilot Type

| Workstream | Simple | Standard | Complex |
| --- | ---: | ---: | ---: |
| Qualification and discovery | `4h` | `6h` | `8h` |
| Infrastructure deployment support | `4h` | `8h` | `15h` |
| Data migration and implementation | `6h` | `8h` | `12h` |
| Ongoing support | `4h` | `6h` | `8h` |
| Closeout and knowledge transfer | `2h` | `2h` | `2h` |
| **Total** | **`20h`** | **`30h`** | **`45h`** |

**Infrastructure Support Details:**
- **Simple**: Deployment docs, troubleshooting, basic guidance (4h)
- **Standard**: Live deployment support, configuration review, best practices (8h)
- **Complex**: Custom Terraform development, hands-on deployment assistance, knowledge transfer (15h)

### Usage-Based Expansion Model

**Automatic Tier Suggestions:**
- System monitors usage patterns and feature adoption
- In-app notifications when approaching tier limits
- Self-service upgrade flow with immediate billing

**Expansion Triggers:**
- **Data Volume**: Approaching feature count limits → upgrade suggestion
- **Protocol Usage**: Using multiple protocols → Professional tier suggestion
- **Performance**: High query volume → Enterprise tier suggestion
- **Support**: Multiple tickets → Priority support upgrade

**Simple Upgrade Paths:**
- **Community → Pro**: $1,000/month for 25x more features + full protocol suite
- **Pro → Enterprise**: +$2,000/month for unlimited scale + priority support
- **Add-On Services**: Deployment support, training, custom development

## Upsell Strategy and Expansion Revenue

### In-Pilot Upsell Triggers

**Performance-Driven Upsells:**
- Customer requests >3x baseline data volume → Standard/Complex tier upgrade
- Customer asks about additional protocols → Multi-protocol upgrade
- Customer needs faster response times → Infrastructure scaling discussion

**Integration-Driven Upsells:**
- Customer wants to connect additional systems → Custom integration services
- Customer needs real-time data sync → Enterprise features discussion
- Customer asks about mobile/web app support → Full-stack engagement

**Timeline-Driven Upsells:**
- Customer wants to accelerate timeline → Premium support upgrade (+$3k)
- Customer requests post-pilot implementation → Annual plan early conversion
- Customer needs production deployment help → Professional services engagement

### Expansion Revenue Framework

**Immediate Pilot Upgrades:**
- Simple → Standard: +$4k (if requested by week 3)
- Standard → Complex: +$6k (if requested by week 3)
- Premium support addon: +$3k (any time)

**Post-Pilot Expansion Paths:**
- **Year 1 Implementation**: $50k-$150k (depends on scale and customization)
- **Professional Services**: $150/hour for custom development
- **Managed Services**: $5k-$15k/month for operational support
- **Enterprise Features**: Custom pricing for advanced capabilities

### Upsell Conversation Framework

**Week 2-3 Check-in:**
- "How does current performance compare to your expectations?"
- "What additional data sources are you thinking about connecting?"
- "Are there other teams that might want to integrate with this?"

**Week 4-5 Expansion Probe:**
- "What would production rollout look like if pilot succeeds?"
- "How are you thinking about long-term operational support?"
- "What's the timeline for implementing this across other departments?"

**Week 6-7 Conversion Setup:**
- "Based on results so far, what's your vision for the next 12 months?"
- "What budget planning do you need to do for full implementation?"
- "Who else needs to be involved in the production deployment decision?"

## Subscription Pricing and Conversion Strategy

### Annual Subscription Model

**Starter Plan - $2k/month ($20k annually)**
- Based on Simple Pilot success criteria
- Single protocol, up to 5M features
- Standard support (email + docs)
- Includes: Security updates, basic monitoring
- **Target conversion**: Simple pilot customers

**Professional Plan - $5k/month ($50k annually)**
- Based on Standard Pilot success criteria
- Multi-protocol, up to 50M features
- Priority support (email + monthly check-in)
- Includes: Performance optimization, backup management
- **Target conversion**: Standard pilot customers

**Enterprise Plan - $12k/month ($120k annually)**
- Based on Complex Pilot success criteria
- Full protocol suite, unlimited features
- Dedicated support (Slack + quarterly reviews)
- Includes: Custom integrations, SLA guarantees, compliance reporting
- **Target conversion**: Complex pilot customers

### Pilot-to-Subscription Conversion Framework

**Conversion Incentives (Present at Week 8 Readout):**
- **Early conversion bonus**: 20% first-year discount if signed within 2 weeks of pilot completion
- **Pilot credit**: 50% of pilot fee applied to first year if converted within 30 days
- **Implementation fast-track**: Free professional services hours for production deployment

**Conversion Timeline:**
- **Immediate (Week 8)**: Present pricing, gather initial feedback
- **Week 9-10**: Address objections, customize package if needed
- **Week 11-12**: Final negotiation and signature
- **Week 13+**: Implementation planning and production deployment

### Pricing Objection Handling

**"Too expensive compared to pilot":**
- Frame as cost-per-feature or cost-per-query improvement
- Show ROI calculation based on pilot success metrics
- Offer phased implementation with gradual scaling

**"Need to compare to alternatives":**
- Provide competitive analysis and TCO comparison
- Highlight unique multi-protocol capabilities
- Offer extended trial period in production environment

**"Budget cycle timing issues":**
- Create bridge proposal with monthly billing option
- Defer start date to align with budget cycle
- Offer pilot extension to maintain momentum

### Success-Based Conversion Metrics

**Conversion probability scoring:**
- **High (80%+)**: Exceeded all success criteria + executive engagement + budget confirmed
- **Medium (50-80%)**: Met success criteria + champion support + budget process underway
- **Low (20-50%)**: Partial success criteria + limited engagement + budget unclear
- **Unlikely (<20%)**: Failed success criteria or no decision-maker access

**Conversion tracking by tier:**
- Track pilot fee → annual subscription multiples (target: 3-6x)
- Monitor time-to-conversion (target: <30 days post-pilot)
- Measure expansion revenue within first year (target: +25% of initial subscription)

## Software Subscription Pricing
*Pure software licensing - deployment handled separately*

### Monthly Subscription Tiers

**Community - FREE**
- Up to 1M features
- REST + OGC APIs
- Community support (docs + forums)
- Basic monitoring dashboards
- **Always free**

**Pro - $1,000/month**
- Up to 25M features
- Full protocol suite (REST + OGC + MVT + OData)
- Email support + monthly check-ins
- Advanced monitoring + alerting
- **Free 30-day trial**

**Enterprise - $3,000/month**
- Unlimited features
- All protocols + custom extensions
- Priority support (Slack + SLA)
- White-label options
- **Free 30-day trial + success manager**

### Deployment Options (Separate from Subscription)

**Self-Service (Free):**
- Cloud marketplace deployment (Community/Pro)
- Terraform templates + documentation (All tiers)

**Professional Services (Custom Pricing):**
- Enterprise installation and optimization
- Custom integrations and architecture review
- Typically $5k-$25k depending on complexity

### Deployment Distribution Strategy

**Lower Tier: Cloud Marketplace (Self-Service)**
- AWS/Azure marketplace for Starter/Professional customers
- One-click deployment with marketplace billing
- CloudFormation/ARM templates (marketplace-optimized)
- Built-in customer discovery and trust

**Mid Tier: Self-Service Terraform**
- GitHub repo with complete Terraform modules
- Comprehensive docs + video tutorials
- Community support via GitHub issues/discussions
- Customer deploys in their own cloud account

**Enterprise: Professional Services (Separate Pricing)**
- Custom installation consultation and implementation
- Architecture review and optimization
- Integration with existing enterprise systems
- Priced separately from software subscription

### Support Add-Ons
- Premium SLA upgrade: **+$3k** (4-hour P1 response, dedicated Slack)
- Infrastructure-only deployment: **+$2k** (Terraform deployment without data migration)
- Extended support hours: **$125/hour** beyond package limits
- Custom integration development: **$150/hour**
- Target minimum pilot gross margin: `50%+` on standard scope

## Procurement and Legal Readiness Checklist
Start during `Week 1` to avoid end-of-pilot delays.

- Security questionnaire owner assigned on both sides
- DPA/MSA reviewers named and timeline agreed
- Procurement sponsor identified with approval path
- Target signature date documented
- Required compliance evidence list captured (for example: architecture diagram, data flow, encryption controls)

## SaaS Metrics Dashboard
Track these weekly:

**Acquisition:**
- Trial signups (website vs. marketplace vs. referral)
- Trial-to-paid conversion rate by tier
- Customer acquisition cost (CAC) by channel
- Time to first value (deployment to first API call)

**Revenue:**
- Monthly Recurring Revenue (MRR) growth
- Revenue per customer by tier
- Upgrade rate (Starter→Professional→Enterprise)
- Churn rate and expansion revenue

**Product Engagement:**
- Trial deployment success rate
- Feature adoption by tier
- API usage patterns and growth
- Support ticket volume and resolution time

**Marketplace Performance:**
- Marketplace listing views and installs
- Marketplace conversion rates
- Cloud provider co-marketing impact

## Operating Cadence
- Monday: pipeline and qualification review
- Midweek: active pilot delivery review
- Friday: metrics + forecast + blockers

## Risk Management and Early Warning System

### High-Priority Risks

**1. Scope Creep Risk**
- **Early warning**: Support hours >80% consumed by week 4
- **Mitigation**: Weekly scope review, mandatory change orders for new requirements
- **Escalation**: Founder approval required for any overrun >20%

**2. Technical Integration Risk**
- **Early warning**: Data access not resolved by week 2
- **Mitigation**: Technical discovery must validate data access path before pilot start
- **Escalation**: CTO involvement for any architectural blockers

**3. Pilot-to-Conversion Risk**
- **Early warning**: Executive sponsor not attending weekly check-ins
- **Mitigation**: Require named sponsor commitment at kickoff
- **Escalation**: Founder-to-executive touchpoint if sponsor disengages

**4. Support Capacity Risk**
- **Early warning**: >3 active complex pilots simultaneously
- **Mitigation**: Stagger complex pilot starts by 2+ weeks
- **Escalation**: Pause new pilot intake if support team at 90% capacity

**5. Scope Creep and Margin Risk**
- **Early warning**: >2 change orders per pilot or >30% hour overrun
- **Mitigation**: Strict change control process, automated billing triggers
- **Escalation**: Founder approval required for any work beyond 150% base scope

**6. Conversion Risk**
- **Early warning**: <50% conversion probability by week 6 of pilot
- **Mitigation**: Weekly executive touchpoints, success metric reviews
- **Escalation**: Founder-led conversation with economic buyer

### Operational Risk Controls

**Weekly Risk Dashboard:**
- Support hour burn rate vs. budget by pilot
- Data access blockers and resolution timeline
- Executive engagement score (meeting attendance, responsiveness)
- Conversion probability based on success metric achievement

**Monthly Risk Review:**
- ICP segment performance analysis
- Pilot pricing tier profitability assessment
- Support model effectiveness and capacity planning
- Case study pipeline and content quality review

## Immediate Next Steps
| Action | Owner | Target Date |
| --- | --- | --- |
| Finalize SaaS pricing tiers and trial limitations | Founder/PM | March 5, 2026 |
| Set up trial environment provisioning automation | Engineering | March 8, 2026 |
| Create AWS/Azure marketplace listings | PM + DevOps | March 12, 2026 |
| Build self-service trial signup and billing flow | Engineering + Ops | March 15, 2026 |
| Launch documentation site and deployment guides | PM + Marketing | March 18, 2026 |
| Test end-to-end trial-to-paid conversion flow | All | March 20, 2026 |
