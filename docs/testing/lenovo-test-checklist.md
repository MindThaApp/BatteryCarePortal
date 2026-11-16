# Lenovo test checklist

For a Lenovo IdeaPad or Yoga. Install steps are in Install.txt next to the package.

## Before you start
1. If Power Plan Manager is installed, turn its battery care off (or uninstall it).
2. Open Lenovo Vantage and write down the current conservation mode (on or off).

## Checks
1. Install the package and open Battery Care Portal.
2. On Overview, check "Device support" says Supported.
3. Press "Copy diagnostics report". Paste it somewhere (Notepad) and check it has text.
4. Turn the charge limit on. You should get at most one administrator prompt, and only if the driver needs it. Confirm in Lenovo Vantage that conservation mode is now on.
5. Turn the limit off. Confirm in Vantage that it is off.
6. Press "Restore laptop defaults". Vantage should show what you wrote down at the start.
7. Turn the limit on again, then quit the app completely. Check in Vantage that it is still on.
8. With the limit on, put the laptop to sleep, wake it, and check Vantage again.

## What to send back
- The diagnostics report (from step 3, or a fresh one after any problem).
- Any error text or message you saw, and which step it happened at.
- What Vantage showed at the start and at the end.
