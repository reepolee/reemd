# LAN clipboard send recovery

## Symptom

macOS reported `HostUnreachable` while sending clipboard text to the configured Windows peer, even though the Windows instance could send to macOS. Later, copying from ReeMD or VS Code produced no outbound clipboard update while receiving from Windows continued to work.

## Cause

Two independent send-side faults caused this behavior:

- `ClipboardSyncService` assigned `_last_clipboard_text` before attempting delivery. A failed TCP connection therefore made the polling loop treat the unchanged clipboard as already synchronized, with no later retry.
- macOS clipboard polling was disabled to avoid blocking Avalonia clipboard reads. This prevented external copies from ever being detected. ReeMD's explicit copy publish also used a zero-timeout send lock, so a busy lock silently discarded the copy.

## Fix

Track the last successfully sent text per peer. A peer that fails to receive the current text remains pending and is retried by the polling loop. Peers that already received it are not sent duplicate updates. The tracking state resets for every new local clipboard value, so each new value is delivered to every configured peer.

On macOS, use the native pasteboard change counter to detect a real change, then read text through `/usr/bin/pbpaste` off the Avalonia UI thread. Explicit ReeMD copy publishes wait for the send lock instead of being discarded.

Replace one TCP connection per clipboard update with one persistent bidirectional connection per peer. Peers discover matching channels through mDNS, exchange a versioned handshake, reuse either the inbound or outbound socket, and acknowledge message IDs. Simultaneous connections are deduplicated deterministically. Received messages are recorded before the system clipboard changes, so they are acknowledged without being echoed back.

## Verification

At the first investigation, `192.168.168.70:45904` accepted a TCP connection from this Mac and responded to ping. The earlier `HostUnreachable` was transient network state; the service now recovers automatically instead of dropping the update.

The macOS regression is covered by separating change detection and text reads from Avalonia's UI clipboard path. An isolated protocol check verifies persistent handshake, bidirectional transfer, acknowledgements, echo prevention, and mDNS discovery.
