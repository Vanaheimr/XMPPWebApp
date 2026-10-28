#!/bin/bash
#
# Start what updateAndBuild.sh built. Everything given here is passed on:
#
#   ./run.sh                     127.0.0.1:8080
#   ./run.sh --any               every address of this machine
#   ./run.sh --any --port 8080
#   ./run.sh --help              what else there is
#
# No version hashes of the libraries as in the run.sh of the deployment
# projects: this program takes named switches only and answers anything else
# with its usage and exit code 2, and its own version it reads off its assembly.
#
# --no-build --no-restore, because updateAndBuild.sh has just built. Which also
# means a source change since then does not reach this start - that is what
# makes it immediate.
#
# What it writes lies in ~/.local/share/XMPPWebApp, not in the working copy;
# the directory this script changes to is for finding the project and the
# frontend of --dev.

cd "$(dirname "$0")"

exec dotnet run --project src/XMPPWebApp/XMPPWebApp.csproj --no-build --no-restore -- "$@"
