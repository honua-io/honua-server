# CSP Enhancement

Use Content Security Policy (CSP) to reduce XSS and injection risks for the Admin UI.

---

## Recommended Approach

1. Start with **report-only** mode.
2. Observe violations and refine allowed domains.
3. Enable enforcement once violations stabilize.

---

## Reporting

Honua exposes a CSP violation endpoint:

```
POST /csp-violation-report
```

Send reports from your edge or UI CSP configuration.

---

## Guidance

- Keep the allowed domain list small and explicit.
- Avoid `unsafe-inline` unless strictly necessary.
- Review CSP whenever you add new UI integrations.

---

## Related Docs

- [Security Configuration](SECURITY_CONFIGURATION.md)
- [Admin UI](ADMIN_UI.md)
