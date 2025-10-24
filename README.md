# SimpleSync

A simple folder synchronization program.

Usage: SimpleSync \<source\> \<destination\> \<check interval in seconds\> \<(optional)path to a log file\>

If a log file path is provided, the program will log any changes to the file system into it.

Remarks:
- If a file in the destination directory is modified after the sync, it will not be overriden unless the source file changes or the program is restarted.
- Similarly the contents of the destination directory will not be touched, unless their relative path matches that of something in the source directory
- This is a deliberate design decision for safety reasons.
- The synchronizaton time is not counted towards the interval, so in practice the real interval is (synchronization time) + interval.
- Access denied on the side of the source should not cause any issues, however if access is denied on the destination side the program will exit with an error.
- Be careful when using any utility that can mass-delete files like this one.
