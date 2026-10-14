"""Run native aggregation, notification and computer-update state fixtures without an Android device.

    python tests/android_aggregate_check.py --jdk <existing JDK directory>
"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jdk", default=os.environ.get("JAVA_HOME"))
    args = parser.parse_args()
    if not args.jdk:
        parser.error("Pass --jdk with the existing JDK directory")
    root = Path(__file__).resolve().parents[1]
    javac = Path(args.jdk) / "bin/javac.exe"
    java = Path(args.jdk) / "bin/java.exe"
    with tempfile.TemporaryDirectory(prefix="remote-cli-aggregate-") as output:
        subprocess.run([str(javac), "-J-Duser.language=en", "-Xlint:-options", "--release", "8", "-encoding", "UTF-8", "-d", output,
                        str(root / "android/src/io/github/kangwang42/remotecli/AggregateSessions.java"),
                        str(root / "android/src/io/github/kangwang42/remotecli/Watch.java"),
                        str(root / "android/src/io/github/kangwang42/remotecli/ComputerUpdate.java"),
                        str(root / "tests/AggregateSessionsCheck.java"), str(root / "tests/WatchCheck.java"),
                        str(root / "tests/ComputerUpdateCheck.java")], check=True)
        subprocess.run([str(java), "-cp", output, "io.github.kangwang42.remotecli.AggregateSessionsCheck"], check=True)
        subprocess.run([str(java), "-cp", output, "io.github.kangwang42.remotecli.WatchCheck"], check=True)
        subprocess.run([str(java), "-cp", output, "io.github.kangwang42.remotecli.ComputerUpdateCheck"], check=True)


if __name__ == "__main__":
    main()
