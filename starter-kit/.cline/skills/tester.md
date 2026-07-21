## Test routine
Tests are executed on the HOST by a human running test scripts outside
your sandbox. You, the agent, must NEVER attempt to run tests, `dotnet
build`, `dotnet test`, `dotnet restore`, or `dotnet format` yourself:
none of them exist in your environment, and trying will only produce
"command not found" errors.

Your responsibilities are exactly two:
1. When a task hands you a test failure log, treat it as ground truth and
   fix the code it describes; do not dispute the log or attempt to
   re-run anything.
2. Never edit anything under the Contracts test project
   (`tests/[ProjectName].Contracts/`), the Golden project, or `tests/fixtures/`
   to make a failure go away. Fix the code, not the test.
