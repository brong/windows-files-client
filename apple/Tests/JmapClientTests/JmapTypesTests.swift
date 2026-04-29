import Foundation
import Testing
@testable import JmapClient

@Test func testFileNodeIsFolder() {
    let folder = FileNode(
        id: "M1", parentId: "M0", blobId: nil, name: "Documents",
        type: nil, size: nil, created: nil, modified: nil, accessed: nil,
        role: nil, executable: nil, isSubscribed: nil, myRights: nil, shareWith: nil
    )
    #expect(folder.isFolder == true)

    let file = FileNode(
        id: "M2", parentId: "M0", blobId: "B1", name: "test.txt",
        type: "text/plain", size: 100, created: nil, modified: nil, accessed: nil,
        role: nil, executable: nil, isSubscribed: nil, myRights: nil, shareWith: nil
    )
    #expect(file.isFolder == false)
}

@Test func testFileNodeRoles() {
    let home = FileNode(
        id: "M1", parentId: nil, blobId: nil, name: "Home",
        type: nil, size: nil, created: nil, modified: nil, accessed: nil,
        role: "home", executable: nil, isSubscribed: nil, myRights: nil, shareWith: nil
    )
    #expect(home.isHome == true)
    #expect(home.isTrash == false)

    let trash = FileNode(
        id: "M2", parentId: nil, blobId: nil, name: "Trash",
        type: nil, size: nil, created: nil, modified: nil, accessed: nil,
        role: "trash", executable: nil, isSubscribed: nil, myRights: nil, shareWith: nil
    )
    #expect(trash.isTrash == true)
    #expect(trash.isHome == false)
}

@Test func testAnyCodableRoundTrip() throws {
    let original: [String: AnyCodable] = [
        "string": "hello",
        "int": 42,
        "bool": true,
        "null": nil,
        "array": [1, 2, 3],
        "nested": ["key": "value"],
    ]

    let encoder = JSONEncoder()
    encoder.outputFormatting = .sortedKeys
    let data = try encoder.encode(original)

    let decoder = JSONDecoder()
    let decoded = try decoder.decode([String: AnyCodable].self, from: data)

    #expect(decoded["string"]?.stringValue == "hello")
    #expect(decoded["int"]?.intValue == 42)
    #expect(decoded["bool"]?.boolValue == true)
    #expect(decoded["array"]?.arrayValue?.count == 3)
    #expect(decoded["nested"]?.dictValue?["key"]?.stringValue == "value")
}

@Test func testSessionFileNodeCapability() throws {
    let json = """
    {
        "apiUrl": "https://api.example.com/jmap/api",
        "downloadUrl": "https://api.example.com/jmap/download/{accountId}/{blobId}/{name}?type={type}",
        "uploadUrl": "https://api.example.com/jmap/upload/{accountId}/",
        "eventSourceUrl": "https://api.example.com/jmap/eventsource",
        "capabilities": {
            "urn:ietf:params:jmap:core": {},
            "https://www.fastmail.com/dev/filenode": {}
        },
        "accounts": {
            "A1": {
                "name": "test@example.com",
                "isPersonal": true,
                "accountCapabilities": {
                    "https://www.fastmail.com/dev/filenode": {}
                }
            }
        },
        "primaryAccounts": {
            "https://www.fastmail.com/dev/filenode": "A1"
        }
    }
    """.data(using: .utf8)!

    let session = try JSONDecoder().decode(JmapSession.self, from: json)
    #expect(session.hasFileNode == true)
    #expect(session.fileNodeAccountId() == "A1")
    #expect(session.fileNodeCapabilityURI == "https://www.fastmail.com/dev/filenode")

    let url = session.downloadURL(accountId: "A1", blobId: "B1", name: "test.txt", type: "text/plain")
    #expect(url?.absoluteString == "https://api.example.com/jmap/download/A1/B1/test.txt?type=text/plain")
}

@Test func testCapabilityURIs() {
    #expect(JmapCapability.core == "urn:ietf:params:jmap:core")
    #expect(JmapCapability.blob == "urn:ietf:params:jmap:blob")
    #expect(JmapCapability.blob2 == "https://www.fastmail.com/dev/blob2")
    #expect(JmapCapability.allFileNodeURIs.count == 2)
}
