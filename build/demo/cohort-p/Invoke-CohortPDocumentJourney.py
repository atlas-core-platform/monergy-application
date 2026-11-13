"""Run the accepted governed document journey against the isolated Cohort P profile.

The reusable Cohort F journey already exercises the public upload, live ClamAV,
durable Evidence, processing, authorization, replay and restart contracts.  This
thin acceptance-only wrapper retargets that harness in memory; it does not copy
credentials, bypass an owner, or alter product implementation.
"""

from __future__ import annotations

import argparse
import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[3]
SOURCE = pathlib.Path(r"E:\Projects\Atlas\monergy-application\build\demo\cohort-f\Invoke-CohortFPhysical.py")


def load() -> dict[str, object]:
    text = SOURCE.read_text(encoding="utf-8")
    replacements = {
        "MONERGY_COHORT_F_REPOSITORY": "MONERGY_COHORT_P_REPOSITORY",
        "MONERGY_COHORT_F_BASE_URL": "MONERGY_COHORT_P_BASE_URL",
        r"E:\Projects\Atlas\monergy-application\.codex-worktrees\p2-cohort-f-acceptance": str(ROOT),
        '"p2f-documents"': '"p2p-durable-documents"',
        '"http://127.0.0.1:4237"': '"http://127.0.0.1:4311"',
        '"cohort-f-v1-reviewed-sources"': '"cohort-p-v1-durable-classification"',
        'ARTIFACT_DIR = ROOT / ".artifacts" / "cohort-f"': 'ARTIFACT_DIR = ROOT / ".artifacts" / "cohort-p"',
    }
    for before, after in replacements.items():
        text = text.replace(before, after)
    namespace: dict[str, object] = {"__name__": "cohort_p_reused_document_journey"}
    exec(compile(text, str(SOURCE), "exec"), namespace)
    return namespace


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("phase", choices=["positive", "negatives", "outages", "post-restart"])
    phase = parser.parse_args().phase
    namespace = load()
    runner = namespace[{"positive": "run_positive", "negatives": "run_negatives", "outages": "run_outages", "post-restart": "run_post_restart"}[phase]]
    result = runner()
    print({"phase": result["phase"], "checks": result["checks"], "passed": True})


if __name__ == "__main__":
    main()
