import tempfile
from pathlib import Path
import unittest

from evaluate import TASKS, grade, summary, transform


def matrix():
    return [dict(task=task, arm=arm, rep=rep, model="m", host="h", candidate="sha",
                 budgetSeconds=30, status="finished", passed=True, seconds=1,
                 inputTokens=17, outputTokens=3, toolCalls=1)
            for task in TASKS for arm in ("control", "tool") for rep in range(1, 4)]


class EvaluationTests(unittest.TestCase):
    def test_missing_or_duplicated_cell_is_not_a_balanced_pair(self):
        rows = matrix()
        self.assertTrue(summary(rows)["gate"])
        self.assertFalse(summary(rows[:-1])["gate"])
        with self.assertRaises(ValueError):
            summary([*rows[:-1], rows[0]])

    def test_loss_cannot_be_hidden_by_other_categories(self):
        rows = matrix()
        rows[-1].update(status="timeout", passed=False, seconds=30, outputTokens=None)
        result = summary(rows)
        self.assertTrue(result["complete"])
        self.assertFalse(result["gate"])
        self.assertIsNone(result["categories"]["text-control"]["tool"]["median"]["outputTokens"])

    def test_changed_model_or_infrastructure_failure_blocks_gate(self):
        rows = matrix()
        rows[0]["model"] = "other model"
        self.assertFalse(summary(rows)["gate"])
        rows[0].update(model="m", status="infra", passed=False)
        self.assertFalse(summary(rows)["gate"])

    def test_all_timeouts_are_not_evidence(self):
        rows = matrix()
        for row in rows:
            row.update(status="timeout", passed=False, seconds=30)
        self.assertFalse(summary(rows)["gate"])

    def test_edit_claim_needs_worktree_and_extra_hits_fail(self):
        with self.assertRaises(ValueError):
            grade(TASKS["A3"], {"renamed": "SessionHistory"})
        with tempfile.TemporaryDirectory() as tmp:
            self.assertFalse(grade(TASKS["F1"], {"sites": ["invented:1"]}, work=Path(tmp))[0])

    def test_a3_reference_edit_changes_only_the_pinned_identifier_and_binding_sites(self):
        # A miniature of the pinned files, with distinct decoys and mixed line endings.
        # The expected bytes are independent of the bank's edit rules.
        def view_model(member):
            lines = {26: f"        {member}.Clear();", 42: f"        {member}.Clear();",
                     69: f"        {member}.Clear();", 90: f"    public Log {member} {{ get; }}",
                     92: "    // Just a helper to add content to ConversationLog",
                     93: '    const string Label = "ConversationLog";', 95: f"        {member}.Add(content);"}
            return b"\xef\xbb\xbf" + "\r\n".join(lines.get(i, "") for i in range(1, 96)).encode()

        def view(member):
            lines = {65: "<!-- ConversationLog -->", 66: f' ItemsSource="{{Binding {member}}}" />',
                     73: f'\tCommand="{{Binding {member}.Clear}}"'}
            return ("\n".join(lines.get(i, "") for i in range(1, 74)) + "\n").encode()

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            cs = root / "ViewModels/CommunityToolkitCommandsViewModel.cs"
            xaml = root / "Views/CommunityToolkitCommandsSampleView.axaml"
            cs.parent.mkdir()
            xaml.parent.mkdir()
            cs.write_bytes(view_model("ConversationLog"))
            xaml.write_bytes(view("ConversationLog"))
            transform(root, TASKS["A3"]["edit"])
            self.assertEqual(cs.read_bytes(), view_model("SessionHistory"))
            self.assertEqual(xaml.read_bytes(), view("SessionHistory"))


if __name__ == "__main__":
    unittest.main()
