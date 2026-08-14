import SwiftUI
import QuadStickKit

struct ModesView: View {
    @Environment(AppModel.self) private var model
    @State private var renaming: Mode?
    @State private var draftName = ""

    var body: some View {
        List {
            Section {
                EmptyView()
            } footer: {
                Text("A profile is the complete setup for a game or activity. A mode is a different control layout inside that setup. The QuadStick tells modes apart by their number, so two modes may share a name.")
            }

            Section {
                ForEach(Array(model.profile.modes.enumerated()), id: \.element.id) { index, mode in
                    modeRow(index: index, mode: mode)
                }
                .onMove { from, to in
                    model.mutate { $0.modes.move(fromOffsets: from, toOffset: to) }
                }
                .onDelete { offsets in
                    guard model.profile.modes.count > offsets.count else { return }
                    model.mutate { $0.modes.remove(atOffsets: offsets) }
                    model.modeIndex = min(model.modeIndex, model.profile.modes.count - 1)
                }
            }

            Section {
                Button("Add Mode", systemImage: "plus") {
                    model.mutate { $0.modes.append(Mode(name: "Mode \($0.modes.count + 1)")) }
                }
            }
        }
        .navigationTitle("Modes")
        .toolbar { EditButton() }
        .alert("Rename Mode", isPresented: renamePresented) {
            TextField("Name", text: $draftName)
            Button("Save") {
                if let mode = renaming, let i = model.profile.modes.firstIndex(where: { $0.id == mode.id }) {
                    model.mutate { $0.modes[i].name = draftName }
                }
                renaming = nil
            }
            Button("Cancel", role: .cancel) { renaming = nil }
        }
    }

    private var renamePresented: Binding<Bool> {
        Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })
    }

    private func modeRow(index: Int, mode: Mode) -> some View {
        let active = index == model.modeIndex
        let assigned = mode.assignments.values.filter { $0.output != nil }.count
        return Button {
            model.modeIndex = index
        } label: {
            HStack(spacing: 12) {
                // Mini LED, same language as the device picture.
                VStack(spacing: 2) {
                    Circle()
                        .fill(active ? Color.green : Color(.systemGray4))
                        .frame(width: 12, height: 12)
                    Text("\(index + 1)").font(.caption2.bold())
                        .foregroundStyle(active ? .green : .secondary)
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text(mode.name).font(.headline).foregroundStyle(.primary)
                    Text("\(assigned) action\(assigned == 1 ? "" : "s") assigned")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                if active {
                    Text("Active").font(.caption).foregroundStyle(.green)
                }
                Button("Rename", systemImage: "pencil") {
                    renaming = mode
                    draftName = mode.name
                }
                .labelStyle(.iconOnly)
                .buttonStyle(.borderless)
                .accessibilityLabel("Rename \(mode.name)")
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Mode \(index + 1), \(mode.name), \(assigned) actions assigned\(active ? ", active" : "")")
        .accessibilityHint("Double tap to make this the active mode")
    }
}
