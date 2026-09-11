@echo off
set CHROME_PATH="C:\Program Files\Google\Chrome\Application\chrome.exe"
if not exist %CHROME_PATH% set CHROME_PATH="C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
start "" %CHROME_PATH% --remote-debugging-port=9222 --user-data-dir="%LOCALAPPDATA%\LumnWindows\ChromeDebugProfile" https://music.youtube.com
