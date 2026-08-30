# LAN clipboard TCP delivery retry

## Symptom

macOS reported `HostUnreachable` while sending clipboard text to the configured Windows peer, even though the Windows instance could send to macOS.

## Cause

`ClipboardSyncService` assigned `_last_clipboard_text` before attempting delivery. A failed TCP connection therefore made the polling loop treat the unchanged clipboard as already synchronized, with no later retry.

## Fix

Track the last successfully sent text per peer. A peer that fails to receive the current text remains pending and is retried by the polling loop. Peers that already received it are not sent duplicate updates. The tracking state resets for every new local clipboard value, so each new value is delivered to every configured peer.

## Verification

At investigation time, `192.168.168.70:45904` accepted a TCP connection from this Mac and responded to ping. The earlier `HostUnreachable` was transient network state; the service now recovers automatically instead of dropping the update.
