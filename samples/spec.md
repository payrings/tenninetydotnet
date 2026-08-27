# TaskManager

A team task-management service where users organise work into projects and tasks.

## Business Rules
- A user registers with a unique email address and authenticates with JWT (15 minute expiry).
- Users belong to teams; each task belongs to exactly one project and is assigned to one user.
- Tasks move through the workflow: TODO → IN_PROGRESS → IN_REVIEW → DONE.
- Only the assignee or a team admin may transition a task.
- Completed tasks are read-only and archived after 30 days.

## Technical Hints
- Tech stack: .NET 10 Web API, EF Core 10, PostgreSQL, Blazor front end.
- Use the Repository Pattern for persistence; all I/O async.
- REST shape: /api/projects/{id}/tasks, /api/tasks/{id}, /api/auth/register, /api/auth/login.
- Domain entities: User, Team, Project, Task, TaskTransition (audit of status changes).

## UI Descriptions
- Dashboard view: list of projects on the left, task board (columns per status) on the right.
- Task detail modal: title, description, assignee dropdown, status buttons, transition history.
