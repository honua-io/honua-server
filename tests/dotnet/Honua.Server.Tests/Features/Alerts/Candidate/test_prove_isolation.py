# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
import json
import unittest

from prove_isolation import parse_known_routes, validate_refusal_audit


class IsolationProofRegressionTests(unittest.TestCase):
    def test_new_http_methods_cannot_drop_out_of_the_denominator(self):
        routes = [f'{method} /api/v{{version:apiVersion}}/admin/alerts/rules' for method in ('PATCH', 'HEAD', 'OPTIONS')]
        source = 'private static readonly string[] KnownAlertRoutes = [' + ','.join(json.dumps(route) for route in routes) + '];'
        self.assertEqual(routes, parse_known_routes(source))

    def test_malformed_known_route_fails_instead_of_disappearing(self):
        with self.assertRaisesRegex(AssertionError, 'Unrecognized known route'):
            parse_known_routes('private static readonly string[] KnownAlertRoutes = ["PATCH not-an-admin-route"];')

    @staticmethod
    def audit(correlation, method='GET', path='/zones'):
        return {'correlationId': correlation, 'resourceType': 'http', 'resourceId': path,
                'action': 'auth.denied', 'outcome': 'Denied',
                'details': json.dumps({'method': method, 'status': 403})}

    def test_duplicate_audit_cannot_hide_an_unaudited_refusal(self):
        with self.assertRaisesRegex(AssertionError, 'individually correlated'):
            validate_refusal_audit({'a': ('GET', '/zones'), 'b': ('POST', '/rules')},
                                   [self.audit('a'), self.audit('a')])

    def test_audit_must_describe_the_correlated_request(self):
        with self.assertRaisesRegex(AssertionError, 'different request'):
            validate_refusal_audit({'a': ('POST', '/rules')}, [self.audit('a')])

    def test_denied_admin_matrix_record_is_an_access_audit(self):
        row = self.audit('a', 'POST', '/rules')
        row['resourceType'] = 'admin'
        row['action'] = 'admin.mutation'
        validate_refusal_audit({'a': ('POST', '/rules')}, [row])

    def test_every_refusal_has_its_own_denied_access_record(self):
        validate_refusal_audit({'a': ('GET', '/zones'), 'b': ('POST', '/rules')},
                               [self.audit('a'), self.audit('b', 'POST', '/rules')])


if __name__ == '__main__':
    unittest.main()
