@echo off
set SteamAppId=892970

REM =====================================================================
REM  Valheim Dedicated Server - Auto-Restart Wrapper
REM
REM  The ServerHeartbeat module inside FiresDiscordIntegration writes its
REM  sentinel to BepInEx\config\FiresDiscordIntegration\server_heartbeat.json.
REM  This bat reads that sentinel, the channel IDs and BotAdminDiscordIds from
REM  com.Fire.FiresDiscordIntegration.cfg, and the bot token from
REM  FiresDiscordIntegration_BotToken.cfg next to valheim_server.exe.
REM
REM  IMPORTANT: save this file as ANSI or UTF-8-WITHOUT-BOM. A UTF-8 BOM
REM  causes cmd.exe to emit `'(BOM)@echo' is not recognized as an internal
REM  or external command` on the very first line. Notepad's "ANSI"
REM  encoding works; VS Code's "UTF-8" (without BOM) also works. The
REM  default "UTF-8 with BOM" does not.
REM
REM  IMPORTANT: do NOT add `setlocal enabledelayedexpansion` to this bat.
REM  The inline PowerShell command below contains '!' chars in strings like
REM  "RESTART DETECTED!" / "Restart flag created!". With delayed expansion
REM  enabled, cmd consumes everything between paired '!' chars as a variable
REM  name lookup, eating the surrounding `{` / `}` braces and breaking the
REM  PowerShell with "Missing closing '}'" parse errors.
REM
REM  BEHAVIOR:
REM    - CRASH: Auto-restarts in 10 seconds (keeps server up)
REM    - RESTART REACTION: Restarts immediately in 10 seconds (BotAdminDiscordIds users only)
REM    - STOP REACTION: Waits for restart command (polls every 15s; BotAdminDiscordIds users only)
REM    - CLEAN SHUTDOWN: Waits for restart command (polls every 15s)
REM
REM  PORTABLE: This batch file runs the server from its own directory,
REM  allowing multiple server instances on the same machine. Each server
REM  folder can have its own copy of this batch file with unique settings.
REM
REM  To restart a stopped server: a user in BotAdminDiscordIds clicks the Restart reaction.
REM  To permanently exit: Close this window or press CTRL-C during startup.
REM =====================================================================

REM Get the directory where this batch file is located
SET "SERVER_DIR=%~dp0"
cd /d "%SERVER_DIR%"

REM ===================================================================
REM  CONFIGURE THESE TO MATCH YOUR SERVER - required before first run.
REM ===================================================================
REM  SERVER_NAME       Display name on the Steam server list (any text).
REM  SERVER_PORT       UDP port to bind. Default Valheim port is 2456.
REM                    Steam also auto-binds PORT+1 (query) and PORT+2.
REM  SERVER_WORLD      World save file basename (no .db extension). The
REM                    file must exist in %USERPROFILE%\AppData\LocalLow\
REM                    IronGate\Valheim\worlds_local\ - or alongside
REM                    valheim_server.exe if you've configured a custom
REM                    world location.
REM  SERVER_PASSWORD   Min 5 chars. Cannot appear inside SERVER_NAME.
REM                    Leave empty for no password (NOT recommended).
REM  SERVER_PUBLIC     1 = list on the Steam Community tab. 0 = hidden
REM                    (direct-IP join only). Most servers want 1.
REM  RESTART_DELAY     Seconds to wait between server crashes before
REM                    auto-restart. 10 is a sensible default.
REM ===================================================================
SET SERVER_NAME=My Valheim Server
SET SERVER_PORT=2456
SET SERVER_WORLD=Dedicated
SET SERVER_PASSWORD=
SET SERVER_PUBLIC=1
SET RESTART_DELAY=10

REM === Optional Valheim Server Settings ===
REM Uncomment and set these if needed:
REM SET SERVER_CROSSPLAY=0
REM SET SERVER_SAVE_INTERVAL=1800
REM SET SERVER_BACKUP_SHORT=2
REM SET SERVER_BACKUP_LONG=12
REM SET SERVER_MODIFIERS=

REM === Build extra args from optional settings ===
SET EXTRA_ARGS=
IF DEFINED SERVER_CROSSPLAY SET EXTRA_ARGS=%EXTRA_ARGS% -crossplay
IF DEFINED SERVER_SAVE_INTERVAL SET EXTRA_ARGS=%EXTRA_ARGS% -saveinterval %SERVER_SAVE_INTERVAL%
IF DEFINED SERVER_BACKUP_SHORT SET EXTRA_ARGS=%EXTRA_ARGS% -backupshort %SERVER_BACKUP_SHORT%
IF DEFINED SERVER_BACKUP_LONG SET EXTRA_ARGS=%EXTRA_ARGS% -backuplong %SERVER_BACKUP_LONG%
IF DEFINED SERVER_MODIFIERS SET EXTRA_ARGS=%EXTRA_ARGS% -modifier %SERVER_MODIFIERS%

REM === Find the Valheim server executable ===
REM This allows the batch to work even if the exe is renamed or in a subfolder
SET "SERVER_EXE="
FOR %%F IN ("valheim_server.exe" "valheim_server_Data\valheim_server.exe") DO (
    IF EXIST "%%~F" SET "SERVER_EXE=%%~F"
)

IF NOT DEFINED SERVER_EXE (
    echo ERROR: Could not find valheim_server.exe in this directory!
    echo Make sure this batch file is in your Valheim dedicated server folder.
    pause
    exit /b 1
)

TITLE %SERVER_NAME% - Auto-Restart Wrapper

:loop
echo.
echo =====================================================================
echo  [%DATE% %TIME%] Starting server: %SERVER_NAME%
echo  Server directory: %SERVER_DIR%
echo  Server executable: %SERVER_EXE%
echo  PRESS CTRL-C to exit permanently
echo =====================================================================
echo.

REM NOTE: Minimum password length is 5 characters and password cant be in the server name.
REM NOTE: You need to make sure ports 2456-2458 are forwarded through your router and firewall.

IF "%SERVER_PASSWORD%"=="" (
    "%SERVER_EXE%" -nographics -batchmode -name "%SERVER_NAME%" -port %SERVER_PORT% -world "%SERVER_WORLD%" -public %SERVER_PUBLIC% %EXTRA_ARGS%
) ELSE (
    "%SERVER_EXE%" -nographics -batchmode -name "%SERVER_NAME%" -port %SERVER_PORT% -world "%SERVER_WORLD%" -password "%SERVER_PASSWORD%" -public %SERVER_PUBLIC% %EXTRA_ARGS%
)

SET EXIT_CODE=%ERRORLEVEL%
echo.
echo =====================================================================
echo  [%DATE% %TIME%] Server exited with code %EXIT_CODE%.
echo.
echo  DEBUG: Checking exit flags and sentinel...

REM Check if this was a mod-triggered restart or stop. Flag and sentinel
REM paths are written by the ServerHeartbeat module inside FiresDiscordIntegration.
SET "RESTART_FLAG=%SERVER_DIR%BepInEx\plugins\restart_requested.flag"
SET "STOP_FLAG=%SERVER_DIR%BepInEx\plugins\stop_requested.flag"
SET "SENTINEL=%SERVER_DIR%BepInEx\config\FiresDiscordIntegration\server_heartbeat.json"
SET "CONFIG=%SERVER_DIR%BepInEx\config\com.Fire.FiresDiscordIntegration.cfg"
REM Bot token lives OUTSIDE BepInEx\config\ so it doesn't get bundled into
REM modpack/profile exports.  Same file the C# side reads via BotTokenFile.
SET "TOKEN_FILE=%SERVER_DIR%FiresDiscordIntegration_BotToken.cfg"

echo  Restart flag: %RESTART_FLAG%
IF EXIST "%RESTART_FLAG%" (
    echo  - EXISTS
) ELSE (
    echo  - NOT FOUND
)

echo  Stop flag: %STOP_FLAG%
IF EXIST "%STOP_FLAG%" (
    echo  - EXISTS
) ELSE (
    echo  - NOT FOUND
)

echo  Sentinel: %SENTINEL%
IF EXIST "%SENTINEL%" (
    echo  - EXISTS, content:
    TYPE "%SENTINEL%"
) ELSE (
    echo  - NOT FOUND
)
echo.

REM Priority 1: Explicit STOP request
IF EXIST "%STOP_FLAG%" (
    echo  Server STOP was requested via Discord reaction.
    TYPE "%STOP_FLAG%"
    DEL "%STOP_FLAG%" >NUL 2>&1
    echo.
    echo  Server STOPPED. Waiting for restart command...
    echo  Click the Restart reaction in Discord to restart, or close this window to exit.
    echo =====================================================================
    goto wait_for_restart
)

REM Priority 2: Explicit RESTART request
IF EXIST "%RESTART_FLAG%" (
    echo  Restart was requested via Discord reaction.
    TYPE "%RESTART_FLAG%"
    DEL "%RESTART_FLAG%" >NUL 2>&1
    echo.
    echo  Restarting in %RESTART_DELAY% seconds...
    echo =====================================================================
    echo.
    timeout /t %RESTART_DELAY% /nobreak
    goto loop
)

REM Priority 3: Check if this was a clean shutdown or crash
REM If sentinel exists and still says "running", it was a crash
REM If sentinel says "stopped", it was a clean shutdown
SET WAS_CRASH=0
IF EXIST "%SENTINEL%" (
    findstr /C:"\"running\"" "%SENTINEL%" >NUL 2>&1
    IF NOT ERRORLEVEL 1 (
        SET WAS_CRASH=1
    )
)

IF %WAS_CRASH%==1 (
    echo  Server CRASHED or was killed unexpectedly.
    echo  Auto-restarting in %RESTART_DELAY% seconds...
    echo  Press CTRL-C to cancel and stop the server.
    echo =====================================================================
    echo.
    timeout /t %RESTART_DELAY% /nobreak
    goto loop
) ELSE (
    echo  Server shut down cleanly.
    echo  Waiting for restart command...
    echo  Click the Restart reaction in Discord to restart, or close this window to exit.
    echo =====================================================================
    goto wait_for_restart
)

:wait_for_restart
REM Poll for restart flag every 15 seconds
IF EXIST "%RESTART_FLAG%" (
    echo.
    echo  Restart requested! Starting server...
    TYPE "%RESTART_FLAG%"
    DEL "%RESTART_FLAG%" >NUL 2>&1
    timeout /t 3 /nobreak
    goto loop
)

REM Check Discord for restart reaction using PowerShell
echo.
echo [%TIME%] Polling Discord for restart reaction...
echo Config: %CONFIG%
echo Sentinel: %SENTINEL%

REM Store output in a temp file so we can see what PowerShell says
SET "PS_OUTPUT=%TEMP%\discord_poll_output.txt"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$emojiEncoded = '%%F0%%9F%%94%%84'; $configPath = '%CONFIG%'; $tokenFilePath = '%TOKEN_FILE%'; $sentinelPath = '%SENTINEL%'; $restartFlagPath = '%RESTART_FLAG%'; if (-not (Test-Path $configPath)) { Write-Output 'ERROR: Config not found at: ' $configPath; return }; if (-not (Test-Path $tokenFilePath)) { Write-Output 'ERROR: Bot token file not found at: ' $tokenFilePath; Write-Output 'Create it at the server root and paste your bot token after BotToken =.'; return }; if (-not (Test-Path $sentinelPath)) { Write-Output 'ERROR: Sentinel not found at: ' $sentinelPath; return }; Write-Output 'Reading bot token...'; $botToken = ''; foreach ($line in (Get-Content $tokenFilePath)) { $stripped = $line.TrimStart(); if ($stripped.StartsWith('#')) { continue }; if ($stripped -match '^\s*BotToken\s*=\s*(.+)$') { $botToken = $matches[1].Trim() } }; if ([string]::IsNullOrWhiteSpace($botToken)) { Write-Output 'ERROR: BotToken empty in token file. Paste your Discord bot token after BotToken = .'; return }; Write-Output 'Reading channel IDs from cfg...'; $config = Get-Content $configPath; $statusChannelId = ''; $managerChannelId = ''; $allowedUserIds = @(); foreach ($line in $config) { if ($line -match '^\s*StatusChannelId\s*=\s*(.+)$') { $statusChannelId = $matches[1].Trim() }; if ($line -match '^\s*ServerManagerChannelId\s*=\s*(.+)$') { $managerChannelId = $matches[1].Trim() }; if ($line -match '^\s*BotAdminDiscordIds\s*=\s*(.*)$') { foreach ($listedId in $matches[1].Split(',')) { if ($listedId.Trim()) { $allowedUserIds += $listedId.Trim() } } } }; $channelId = if ([string]::IsNullOrWhiteSpace($managerChannelId)) { $statusChannelId } else { $managerChannelId }; if ([string]::IsNullOrWhiteSpace($channelId)) { Write-Output 'ERROR: Channel ID not found in config (looked for ServerManagerChannelId or StatusChannelId)'; return }; if ($allowedUserIds.Count -eq 0) { Write-Output 'BotAdminDiscordIds is empty in the cfg, so restart reactions are ignored. Add the Discord user IDs allowed to restart the server.'; return }; Write-Output \"Bot token loaded ($($botToken.Length) chars)\"; Write-Output \"Channel ID: $channelId\"; $sentinelJson = (Get-Content $sentinelPath -Raw | ConvertFrom-Json); $messageId = $sentinelJson.messageId; if ([string]::IsNullOrWhiteSpace($messageId)) { Write-Output 'ERROR: No message ID in sentinel'; return }; Write-Output \"Message ID: $messageId\"; $url = \"https://discord.com/api/v10/channels/$channelId/messages/$messageId/reactions/$emojiEncoded`?limit=100\"; Write-Output \"URL: $url\"; $headers = @{ 'Authorization' = \"Bot $botToken\"; 'User-Agent' = 'ValheimServerWrapper/1.0' }; try { $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get -TimeoutSec 10 -ErrorAction Stop; Write-Output \"Found $($response.Count) reaction(s)\"; if ($response -and $response.Count -gt 0) { foreach ($user in $response) { if ($user.bot) { continue }; if ($allowedUserIds -notcontains $user.id) { Write-Output ('  - ' + $user.username + ' (' + $user.id + ') ignored: not in BotAdminDiscordIds'); continue }; Write-Output \"  - $($user.username) (RESTART DETECTED!)\"; $content = \"Restart requested by $($user.username) at $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')) UTC\"; Set-Content -Path $restartFlagPath -Value $content -Force; Write-Output 'Restart flag created!'; return }; Write-Output 'No restart reaction from a user in BotAdminDiscordIds' } else { Write-Output 'No reactions found' } } catch { $statusCode = 'Unknown'; $errorBody = 'No details'; try { $statusCode = $_.Exception.Response.StatusCode.value__; $stream = $_.Exception.Response.GetResponseStream(); $reader = New-Object System.IO.StreamReader($stream); $errorBody = $reader.ReadToEnd(); $reader.Close(); $stream.Close() } catch { }; Write-Output \"ERROR: HTTP $statusCode\"; Write-Output \"Details: $errorBody\"; Write-Output \"Exception: $($_.Exception.Message)\"; if ($statusCode -eq 403 -or $statusCode -eq 404) { Write-Output 'Trying to find latest status message...'; try { $messagesUrl = \"https://discord.com/api/v10/channels/$channelId/messages`?limit=10\"; $messages = Invoke-RestMethod -Uri $messagesUrl -Headers $headers -Method Get -TimeoutSec 10; foreach ($msg in $messages) { if ($msg.author.bot -and $msg.embeds -and $msg.embeds[0].title -like '*Server*') { $newMessageId = $msg.id; Write-Output \"Found status message: $newMessageId\"; $newUrl = \"https://discord.com/api/v10/channels/$channelId/messages/$newMessageId/reactions/$emojiEncoded`?limit=100\"; $newResponse = Invoke-RestMethod -Uri $newUrl -Headers $headers -Method Get -TimeoutSec 10; if ($newResponse -and $newResponse.Count -gt 0) { foreach ($user in $newResponse) { if ($user.bot) { continue }; if ($allowedUserIds -notcontains $user.id) { Write-Output ('  - ' + $user.username + ' (' + $user.id + ') ignored: not in BotAdminDiscordIds'); continue }; Write-Output \"  - $($user.username) (RESTART DETECTED!)\"; $content = \"Restart requested by $($user.username) at $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')) UTC\"; Set-Content -Path $restartFlagPath -Value $content -Force; Write-Output 'Restart flag created!'; return } }; break } } } catch { Write-Output \"Fallback failed: $($_.Exception.Message)\" } } }" > "%PS_OUTPUT%" 2>&1

REM PowerShell command completed - show output and continue
IF ERRORLEVEL 1 (
    REM PowerShell returned error, but that's OK - continue polling
    SET PS_ERROR=1
) ELSE (
    SET PS_ERROR=0
)

REM Show what PowerShell said
TYPE "%PS_OUTPUT%"
DEL "%PS_OUTPUT%" >NUL 2>&1

REM Check if restart flag was created
IF EXIST "%RESTART_FLAG%" (
    echo.
    echo Restart flag detected! Starting server...
    TYPE "%RESTART_FLAG%"
    DEL "%RESTART_FLAG%" >NUL 2>&1
    timeout /t 3 /nobreak
    goto loop
)

REM Wait 15 seconds before checking again
echo Waiting 15 seconds before next check...
timeout /t 15 /nobreak >NUL
goto wait_for_restart
