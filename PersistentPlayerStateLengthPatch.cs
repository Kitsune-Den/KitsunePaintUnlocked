// PersistentPlayerStateLengthPatch - INTENTIONALLY EMPTY
//
// Previously patched NetPackagePersistentPlayerState.GetLength from 1000 to 65536.
// This was WRONG: GetLength() returns the EXACT packet byte count, not a buffer size.
// NetPackageManager reads exactly GetLength() bytes from the stream. If write() produces
// fewer bytes than GetLength() declares, the reader over-consumes and desyncs the stream.
//
// The original overflow issue was caused by old player .ttp save files exceeding 1000 bytes,
// not by the custom paint count. Deleting stale .ttp files is the correct fix.
// On fresh worlds, persistent player data fits well within 1000 bytes.
