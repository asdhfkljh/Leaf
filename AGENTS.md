# Version control workflow

This Unity project is managed in https://github.com/asdhfkljh/Leaf.git.

- Before editing, inspect Git status and preserve existing user changes.
- Before a new task changes project files, create a checkpoint commit if there are uncommitted project changes. Do not commit credentials or generated caches.
- After completing and verifying a task, commit the task changes with a clear description and push to the current branch's upstream. If push fails, report that local commits still need syncing.
- Keep Unity assets and their .meta files together. Do not commit Library, Temp, Logs, or UserSettings.
- When asked to undo a committed change, prefer git revert and push the resulting commit. Do not force-push or discard unrelated work.
