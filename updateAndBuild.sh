#!/bin/bash
#
# Pull, put the submodules on the revisions this one records, build.
#
# The pins and not the tips - the one difference from the same script in the
# deployment projects, where the libraries are always wanted at their tip. Here
# every pin is a state both suites have run against. A submodule reached by
# "git submodule foreach git pull" stands ahead of its pin, in a combination
# nothing was measured in: that is how a package downgrade (NU1605) stopped a
# build on 2026-10-27, the application naming Microsoft.Extensions.Logging
# 10.0.13 while Hermod and Ratatoskr had moved on to 10.0.14. Pass --tips to do
# it anyway, which is what trying a library before pinning it looks like.
#
# The project and not XMPPWebApp.slnx: the solution carries RatatoskrTests,
# HermodTests and StyxTests, which is minutes of building for something no start
# needs. "dotnet test" names them when they are wanted.
#
# The frontend comes along by itself - npm ci on the first build, npm run build
# whenever its sources changed, both as targets of this project.

set -e

cd "$(dirname "$0")"

git pull

if [[ "$1" == "--tips" ]]; then
    echo ""
    echo "Submodules to their upstream tips, ahead of the pins of this revision!"
    echo ""
    git submodule foreach git pull
else
    git submodule update --init --recursive
fi

dotnet build src/XMPPWebApp/XMPPWebApp.csproj
