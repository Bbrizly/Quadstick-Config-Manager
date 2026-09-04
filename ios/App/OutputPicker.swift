import SwiftUI
import QuadStickKit

/// Choosing what an input does, out of the 400 words the device accepts.
///
/// This was a nested menu. A menu with 215 keyboard entries in one scroll is
/// not usable by anyone, and it is impossible for someone driving the phone
/// with a mouth stick or Voice Control, which is who this app is for. A sheet
/// with a search field is one target and one word away from any action.
struct OutputPicker: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    let actionID: String
    @State private var query = ""

    private var selected: OutputAction? { model.assignment(for: actionID).output }

    /// The handful people reach for first, so the list opens useful instead of
    /// opening at "Adaptive Controller Left A".
    private static let commonIDs = [
        "controller-a", "controller-b", "controller-x", "controller-y",
        "controller-left-trigger", "controller-right-trigger",
        "controller-left-bumper", "controller-right-bumper",
        "controller-d-pad-up", "controller-d-pad-down",
        "controller-d-pad-left", "controller-d-pad-right",
        "controller-start", "controller-select",
        "mode & profile-next-mode", "mode & profile-previous-mode",
    ]

    private var matches: [OutputAction] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return [] }
        return QuadStickCatalog.outputs.filter {
            $0.name.lowercased().contains(q)
                || (Firmware.keyword(forOutput: $0.id)?.lowercased().contains(q) ?? false)
        }
    }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    row(nil)
                }

                if !query.isEmpty {
                    Section("\(matches.count) \(matches.count == 1 ? "match" : "matches")") {
                        ForEach(matches) { row($0) }
                    }
                } else {
                    Section("Common") {
                        ForEach(Self.commonIDs.compactMap(QuadStickCatalog.output)) { row($0) }
                    }
                    ForEach(OutputCategory.allCases, id: \.self) { category in
                        let items = QuadStickCatalog.outputs.filter { $0.category == category }
                        if !items.isEmpty {
                            Section(category.rawValue) {
                                ForEach(items) { row($0) }
                            }
                        }
                    }
                }
            }
            .listStyle(.insetGrouped)
            .searchable(text: $query, prompt: "Search actions")
            .navigationTitle("What it does")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
        }
    }

    @ViewBuilder
    private func row(_ output: OutputAction?) -> some View {
        let isSelected = output?.id == selected?.id
        Button {
            var a = model.assignment(for: actionID)
            a.output = output
            model.setAssignment(a, for: actionID)
            dismiss()
        } label: {
            HStack(spacing: 12) {
                OutputGlyph(action: output, promptStyle: model.profile.controllerType.promptStyle)
                VStack(alignment: .leading, spacing: 2) {
                    Text(output?.name ?? "Not set")
                        .foregroundStyle(.primary)
                    // The firmware's own word, so someone comparing against a
                    // shared file or the manual can see they match. Hidden when
                    // it is the same word twice.
                    if let word = output.flatMap({ Firmware.keyword(forOutput: $0.id) }),
                       word.caseInsensitiveCompare(output?.name ?? "") != .orderedSame {
                        Text(word)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                Spacer(minLength: 0)
                if isSelected {
                    Image(systemName: "checkmark")
                        .foregroundStyle(Theme.accent)
                }
            }
            .frame(minHeight: 44)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(output?.name ?? "Not set")
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }
}
