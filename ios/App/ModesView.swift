import SwiftUI
import QuadStickKit

struct ModesView: View {
    @Environment(AppModel.self) private var model
    @State private var renaming: Mode?
    @State private var draftName = ""
    @State private var lastModeAlert = false

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
                    model.mutateModes { $0.move(fromOffsets: from, toOffset: to) }
                }
                .onDelete { offsets in
                    guard model.profile.modes.count > offsets.count else {
                        lastModeAlert = true
                        return
                    }
                    model.mutateModes { $0.remove(atOffsets: offsets) }
                }
            }

            Section {
                Button("Add Mode", systemImage: "plus") {
                    model.mutateModes { $0.append(Mode(name: "Mode \($0.count + 1)")) }
                }
            }
        }
        .navigationTitle("Modes")
        .toolbar { EditButton() }
        .alert("Rename Mode", isPresented: renamePresented) {
            TextField("Name", text: $draftName)
            Button("Save") {
                // An empty name leaves the mode with nothing to call it in the
                // picker or the file, so the old name stands.
                let name = draftName.trimmingCharacters(in: .whitespaces)
                if !name.isEmpty, let mode = renaming, let i = model.indexOfMode(mode.id) {
                    model.mutateModes { $0[i].name = name }
                }
                renaming = nil
            }
            Button("Cancel", role: .cancel) { renaming = nil }
        }
        .alert("A profile needs at least one mode.", isPresented: $lastModeAlert) {
            Button("OK", role: .cancel) {}
        } message: {
            Text("The QuadStick has nothing to load without one. Add another mode first, then delete this one.")
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
                        .foregroundStyle(active ? Color.green : Color(white: 0.65))
                }
                VStack(alignment: .leading, spacing: 2) {
                    // Concrete colours: the row Button tints its label, and the
                    // hierarchical styles resolve against that tint, not the theme.
                    Text(mode.name).font(.headline).foregroundStyle(.white)
                    Text("\(assigned) action\(assigned == 1 ? "" : "s") assigned")
                        .font(.caption)
                        .foregroundStyle(Color(white: 0.65))
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
