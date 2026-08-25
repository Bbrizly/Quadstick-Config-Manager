import Foundation

/// The firmware's own word lists, read from the same validation.json the
/// desktop app embeds. Hand-typing these names is how the two apps drift, so
/// the file is copied, never retyped. KitTests fails if the copy falls behind.
public enum Vocabulary {

    private struct Payload: Decodable {
        let inputs: [String]
        let outputs_ps3: [String]
        let outputs_xbox: [String]
        let functions: [String]
    }

    private static let payload: Payload = {
        guard let url = Bundle.module.url(forResource: "validation", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let decoded = try? JSONDecoder().decode(Payload.self, from: data) else {
            // A missing resource means a broken build, not a user error. Empty
            // lists would silently turn every real output into "unknown", so
            // say it loudly instead.
            fatalError("validation.json is missing from the QuadStickKit bundle")
        }
        return decoded
    }()

    public static var inputs: [String] { payload.inputs }
    public static var functions: [String] { payload.functions }

    /// Outputs the device accepts. The two lists overlap almost entirely; the
    /// union is what the editor offers, and `accepts` is what the validator
    /// checks against, so a name valid on either controller is never called
    /// wrong.
    public static let outputNames: [String] = {
        var seen = Set<String>()
        return (payload.outputs_ps3 + payload.outputs_xbox).filter { seen.insert($0).inserted }
    }()

    private static let outputSet = Set(outputNames)

    public static func accepts(output name: String) -> Bool {
        outputSet.contains(name)
    }
}
