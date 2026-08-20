#!/usr/bin/env python3
"""Every tracked file and folder Unity imports must have a committed `.meta`.

Why this is a CI gate rather than a code-review habit: a missing `.meta` does not
fail a build, does not throw, and does not appear in this repository's own test
results. It cost this package twice in one week.

v0.14.0 and v0.15.0 shipped `Tests/Editor/HeldMovementParityTests.cs` with no
`.meta`. Two things followed, and the second is why this is a gate:

  1. Unity ignored the asset, so that parity test **never ran anywhere** -- not
     here, not downstream. A test that ships and is silently absent.

  2. Unity logs it as an Error, and the test framework turns an unexpected log
     error into an UnhandledLogMessageException. That fails a CONSUMER'S ENTIRE
     SUITE. `com.cuvara.dots` hit it with 137/137 EditMode and 29/29 PlayMode
     passing and not one failing test -- the state in which most people conclude
     the runner is flaky and re-run it. A PlayMode measurement on the project was
     also lost to what looked like a hung machine.

So the blast radius of a missing `.meta` is not this repository. It is every
project that installs this package.

Unity ignores paths beginning with a dot and folders ending in `~`, so those are
skipped here for the same reason Unity skips them -- which is also why
`Samples~` is out of scope.
"""

import subprocess
import sys

SKIP_SUFFIXES = (".meta",)


def unity_visible(path):
    parts = path.split("/")
    for part in parts:
        if part.startswith("."):
            return False
    # Samples~, Documentation~ and friends are not imported.
    for part in parts[:-1]:
        if part.endswith("~"):
            return False
    return not parts[-1].endswith("~")


def main():
    tracked = subprocess.run(
        ["git", "ls-files"], capture_output=True, text=True, check=True
    ).stdout.splitlines()

    have = set(tracked)
    missing = []
    directories = set()

    for path in tracked:
        if not unity_visible(path) or path.endswith(SKIP_SUFFIXES):
            continue

        if path + ".meta" not in have:
            missing.append(path)

        # Directories are implied by their contents; git tracks no directory
        # entries of its own, and Unity needs a .meta for each one.
        parts = path.split("/")
        for i in range(1, len(parts)):
            directories.add("/".join(parts[:i]))

    for directory in sorted(directories):
        if not unity_visible(directory):
            continue
        if directory + ".meta" not in have:
            missing.append(directory + "/")

    if missing:
        print(f"{len(missing)} tracked path(s) have no committed .meta:")
        for path in sorted(missing):
            print(f"  {path}")
            print(f"::error file={path.rstrip('/')}::Missing {path.rstrip('/')}.meta — Unity will not import this")
        return 1

    checked = sum(1 for p in tracked if unity_visible(p) and not p.endswith(".meta"))
    print(f"All {checked} Unity-visible tracked file(s) and {len(directories)} folder(s) have a .meta.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
