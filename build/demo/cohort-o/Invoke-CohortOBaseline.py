"""Run the already-accepted Cohort M Net Worth physical harness against Cohort O.

Only constants identifying the isolated retained profile are changed. The owner
journeys, assertions and persisted proof remain byte-for-byte the accepted M
harness, so Cohort O reuses a genuine governed T1-90 baseline.
"""
from pathlib import Path

template = Path(r"E:\Projects\Atlas\monergy-application\build\demo\cohort-m\Invoke-CohortMPhysical.py")
source = template.read_text(encoding="utf-8")
replacements = {
    r"E:\Projects\Atlas\monergy-application\.codex-worktrees\p2-cohort-m-acceptance":
        r"E:\Projects\Atlas\monergy-application\.codex-worktrees\p2-cohort-o-acceptance",
    'p2m-net-worth': 'p2o-customer-completion',
    'http://127.0.0.1:4283': 'http://127.0.0.1:4301',
    'monergy-p2m-20261111': 'monergy-p2o-20261112',
    'cohort-m-v1-complete-net-worth': 'cohort-o-v1-reused-net-worth',
    '.artifacts" / "cohort-m"': '.artifacts" / "cohort-o"',
    '"127.0.0.1", 4283': '"127.0.0.1", 4301',
    'cohort_m_forbidden': 'cohort_o_forbidden',
    'Cohort M': 'Cohort O baseline',
    'cohort-m-': 'cohort-o-',
    'cohort-m-': 'cohort-o-',
}
for old, new in replacements.items():
    source = source.replace(old, new)
exec(compile(source, str(template), "exec"), globals(), globals())
