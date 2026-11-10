"""Drive the real admin OIDC broker and private proposal review for the journey."""
import http.cookiejar
from http import HTTPStatus
import json
import ssl
import urllib.error
import urllib.request
from urllib.parse import parse_qs, urlsplit


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *_):
        return None


class HumanApprovalClient:
    def __init__(self, base, opener):
        self.base = base
        self.opener = opener

    @classmethod
    def login(cls, base, certificate):
        context = ssl.create_default_context(cafile=str(certificate))
        opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context),
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        client = cls(base, opener)
        begin = client.request("/api/v1/admin/auth/providers/oidc/authorize-url", {})
        authorization = urlsplit(begin["authorizeUrl"])
        browser = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context), NoRedirect())
        authorize_url = f"https://127.0.0.1:{authorization.port}{authorization.path}?{authorization.query}"
        try:
            browser.open(authorize_url, timeout=30)
            raise AssertionError("identity provider did not redirect")
        except urllib.error.HTTPError as response:
            if response.code != HTTPStatus.FOUND:
                raise AssertionError("identity authorization failed") from None
            callback = urlsplit(response.headers["Location"])
        assert callback._replace(query="").geturl() == base + "/admin/auth/callback", "unexpected identity callback"
        query = parse_qs(callback.query)
        client.request("/api/v1/admin/auth/providers/oidc/token", {
            "grantType": "authorization_code", "code": query["code"][0], "state": query["state"][0]})
        assert client.request("/api/v1/admin/auth/session")["isAuthenticated"], "interactive session was not established"
        return client

    def request(self, path, payload=None, *, headers=None):
        request_headers = {"Accept": "application/json", **(headers or {})}
        content = None if payload is None else json.dumps(payload).encode()
        if content is not None:
            request_headers["Content-Type"] = "application/json"
        request = urllib.request.Request(self.base + path, data=content, headers=request_headers)
        with self.opener.open(request, timeout=30) as response:
            raw = response.read()
            return json.loads(raw) if raw else {}

    def approve(self, proposal_id):
        path = f"/api/v1/admin/proposals/{proposal_id}"
        snapshot = self.request(path + "/approval-review")
        proposal, review = snapshot["proposal"], snapshot["review"]
        assert (proposal["proposalId"] == review["proposalId"] == proposal_id
                and proposal["version"] == review["proposalVersion"]
                and proposal["planHash"] == review["planHash"]), "proposal review binding differs"
        return self.request(path + "/approve", {}, headers={"X-Honua-Approval-Capability": review["capability"]})
