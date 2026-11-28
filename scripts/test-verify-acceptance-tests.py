import importlib.util
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location(
    "verify_acceptance_tests", Path(__file__).with_name("verify-acceptance-tests.py")
)
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)


class AcceptanceEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.trx = Path(self.directory.name) / "test.trx"

    def write_results(self, methods):
        definitions = []
        results = []
        for index, (class_name, method, outcome) in enumerate(methods):
            definitions.append(
                f'<UnitTest id="{index}"><TestMethod className="MockProxyGateway.Tests.{class_name}" '
                f'name="{method}" /></UnitTest>'
            )
            results.append(f'<UnitTestResult testId="{index}" outcome="{outcome}" />')
        self.trx.write_text(
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            '<TestDefinitions>' + ''.join(definitions) + '</TestDefinitions>'
            '<Results>' + ''.join(results) + '</Results></TestRun>', encoding="utf-8"
        )

    def test_unrelated_passing_test_does_not_establish_acceptance(self):
        self.write_results([("GatewayTests", "Unrelated", "Passed")])
        with self.assertRaisesRegex(ValueError, "GatewayTests.Required"):
            verify.require_methods({"GatewayTests.Required"}, verify.executed_methods([self.trx]))

    def test_passed_theory_rows_are_counted_by_method(self):
        self.write_results([
            ("GatewayTests", "Required", "Passed"),
            ("GatewayTests", "Required(arg: 2)", "Passed"),
            ("ForwardingTests", "MockProxyGateway.Tests.ForwardingTests.Required", "Passed"),
        ])
        actual = verify.executed_methods([self.trx])
        verify.require_methods({"GatewayTests.Required", "ForwardingTests.Required"}, actual)
        self.assertEqual(2, actual["GatewayTests.Required"])
        self.assertEqual(1, actual["ForwardingTests.Required"])

    def test_nonpassing_theory_row_is_not_accepted(self):
        self.write_results([
            ("GatewayTests", "Required", "Passed"),
            ("GatewayTests", "Required(arg: 2)", "NotExecuted"),
        ])
        with self.assertRaisesRegex(ValueError, "Non-passing test result"):
            verify.executed_methods([self.trx])

    def test_missing_results_are_not_accepted(self):
        self.write_results([])
        with self.assertRaisesRegex(ValueError, "No test results"):
            verify.executed_methods([self.trx])


if __name__ == "__main__":
    unittest.main()
