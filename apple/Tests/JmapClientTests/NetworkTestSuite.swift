import Testing

/// Serialized container for all tests that share MockURLProtocol.handler.
/// Nesting JmapClientAPITests and SyncEngineTests here prevents concurrent
/// handler overwrites between suites.
@Suite(.serialized) struct NetworkTests {}
