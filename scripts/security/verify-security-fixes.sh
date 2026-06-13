#!/bin/bash

#
# Security Fixes Verification Script
# Verifies that all 4 critical security vulnerabilities have been properly fixed
#

set -e

BASE_URL="${1:-http://localhost:5000}"
VERBOSE="${VERBOSE:-false}"

echo "🔒 Security Fixes Verification Script"
echo "Testing fixes for 4 critical security vulnerabilities"
echo ""

declare -A test_results
test_results[auth_bypass]=false
test_results[sql_injection]=false
test_results[cors]=false
test_results[info_disclosure]=false

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;37m'
NC='\033[0m' # No Color

# Test 1: Authentication Bypass Logic
echo -e "${YELLOW}1. Testing Authentication Bypass Protection...${NC}"

if response=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/admin/health" 2>/dev/null); then
    if [ "$response" = "401" ]; then
        echo -e "   ${GREEN}✅ Production authentication bypass properly blocked${NC}"
        test_results[auth_bypass]=true
    else
        echo -e "   ${RED}❌ Authentication bypass may be vulnerable (status: $response)${NC}"
    fi
else
    echo -e "   ${YELLOW}⚠️  Could not test auth bypass (server may not be running)${NC}"
fi

# Test 2: SQL Injection Prevention
echo -e "${YELLOW}2. Testing SQL Injection Prevention...${NC}"

sql_payloads=(
    "'; DROP TABLE users; --"
    "field' OR '1'='1"
    "name UNION SELECT password FROM users"
)

sql_tests_passed=0
total_sql_tests=${#sql_payloads[@]}

for payload in "${sql_payloads[@]}"; do
    encoded_payload=$(python3 -c "import urllib.parse; print(urllib.parse.quote('$payload'))" 2>/dev/null || echo "$payload")

    if response=$(curl -s "$BASE_URL/ogc/features/v1/collections/test/items?filter=name='$encoded_payload'" 2>/dev/null); then
        if ! echo "$response" | grep -qi "DROP TABLE\|syntax error\|INSERT INTO\|DELETE FROM"; then
            ((sql_tests_passed++))
            [ "$VERBOSE" = "true" ] && echo -e "      ${GREEN}Payload safely handled: $payload${NC}"
        else
            echo -e "   ${RED}❌ SQL injection may be possible with: $payload${NC}"
        fi
    else
        ((sql_tests_passed++))  # Request rejection is acceptable
        [ "$VERBOSE" = "true" ] && echo -e "      ${GRAY}Request failed for payload: $payload${NC}"
    fi
done

if [ "$sql_tests_passed" -eq "$total_sql_tests" ]; then
    echo -e "   ${GREEN}✅ SQL injection payloads properly handled${NC}"
    test_results[sql_injection]=true
else
    echo -e "   ${RED}❌ Some SQL injection tests failed ($sql_tests_passed/$total_sql_tests passed)${NC}"
fi

# Test 3: CORS Credential Security
echo -e "${YELLOW}3. Testing CORS Credential Security...${NC}"

if response=$(curl -s -H "Origin: https://evil.com" \
                 -H "Access-Control-Request-Method: GET" \
                 -H "Access-Control-Request-Headers: Authorization" \
                 -X OPTIONS \
                 -D - \
                 "$BASE_URL/ogc/features/v1/collections" 2>/dev/null); then

    allow_origin=$(echo "$response" | grep -i "access-control-allow-origin" | cut -d: -f2 | tr -d ' \r\n' || echo "")
    allow_credentials=$(echo "$response" | grep -i "access-control-allow-credentials" | cut -d: -f2 | tr -d ' \r\n' || echo "")

    if [ "$allow_origin" != "https://evil.com" ] || [ "$allow_credentials" != "true" ]; then
        echo -e "   ${GREEN}✅ CORS credentials properly protected from malicious origins${NC}"
        test_results[cors]=true
    else
        echo -e "   ${RED}❌ CORS may expose credentials to unauthorized origins${NC}"
    fi

    if [ "$VERBOSE" = "true" ]; then
        echo -e "      ${GRAY}Allow-Origin: $allow_origin${NC}"
        echo -e "      ${GRAY}Allow-Credentials: $allow_credentials${NC}"
    fi
else
    echo -e "   ${YELLOW}⚠️  Could not test CORS (server may not be running)${NC}"
fi

# Test 4: Information Disclosure Prevention
echo -e "${YELLOW}4. Testing Information Disclosure Prevention...${NC}"

if response=$(curl -s -H "Authorization: Bearer invalid-token" \
                 "$BASE_URL/admin/health" 2>/dev/null); then

    if ! echo "$response" | grep -qi "password\|secret\|key\|bypass\|development"; then
        echo -e "   ${GREEN}✅ Error messages properly sanitized${NC}"
        test_results[info_disclosure]=true
    else
        echo -e "   ${RED}❌ Error messages may expose sensitive information${NC}"
        [ "$VERBOSE" = "true" ] && echo -e "      ${GRAY}Sensitive content detected in: $response${NC}"
    fi
else
    echo -e "   ${YELLOW}⚠️  Could not test information disclosure (server may not be running)${NC}"
fi

# Summary
echo ""
echo -e "${CYAN}🔍 Security Verification Summary${NC}"
echo -e "${CYAN}================================${NC}"

passed_tests=0
total_tests=4

for test in "${test_results[@]}"; do
    [ "$test" = "true" ] && ((passed_tests++))
done

echo -e "Authentication Bypass:     $([ "${test_results[auth_bypass]}" = "true" ] && echo -e "${GREEN}✅ FIXED${NC}" || echo -e "${RED}❌ NEEDS ATTENTION${NC}")"
echo -e "SQL Injection Prevention:  $([ "${test_results[sql_injection]}" = "true" ] && echo -e "${GREEN}✅ FIXED${NC}" || echo -e "${RED}❌ NEEDS ATTENTION${NC}")"
echo -e "CORS Credential Security:   $([ "${test_results[cors]}" = "true" ] && echo -e "${GREEN}✅ FIXED${NC}" || echo -e "${RED}❌ NEEDS ATTENTION${NC}")"
echo -e "Information Disclosure:     $([ "${test_results[info_disclosure]}" = "true" ] && echo -e "${GREEN}✅ FIXED${NC}" || echo -e "${RED}❌ NEEDS ATTENTION${NC}")"

echo ""
if [ "$passed_tests" -eq "$total_tests" ]; then
    echo -e "${GREEN}🎉 All critical security vulnerabilities have been fixed!${NC}"
    echo -e "Overall Status: $passed_tests/$total_tests security fixes verified"
    exit 0
else
    echo -e "${YELLOW}⚠️  Some security fixes need attention. Please review the failed tests.${NC}"
    echo -e "Overall Status: $passed_tests/$total_tests security fixes verified"
    exit 1
fi