import SwiftUI
import QuadStickKit

/// One physical input: each action row assigns straight from a categorized
/// dropdown. The behavior editor is one tap deeper, only when needed.
struct InputDetailView: View {
    @Environment(AppModel.self) private var model
    let input: DeviceInput

    var body: some View {
        List {
            if let detail = input.detail {
                Section { EmptyView() } footer: { Text(detail) }
            }
            Section("In \(model.mode.name) mode") {
                ForEach(input.actions) { action in
                    ActionRow(action: action)
                }
            }
        }
        .navigationTitle(input.name)
        .navigationBarTitleDisplayMode(.large)
    }
}

private struct ActionRow: View {
    @Environment(AppModel.self) private var model
    let action: InputActionDef

    var body: some View {
        let assignment = model.assignment(for: action.id)
        HStack(spacing: 12) {
            NavigationLink(value: action.id) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(action.name)
                    if let f = assignment.function {
                        Text(f.summary)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .buttonStyle(.borderless)
            .accessibilityLabel("\(action.name)\(assignment.function.map { ", \($0.summary)" } ?? ""). Opens behavior and naming.")

            Spacer(minLength: 8)

            OutputMenu(actionID: action.id) {
                HStack(spacing: 4) {
                    Text(assignment.display)
                        .foregroundStyle(assignment.output == nil ? Color.secondary : Theme.accent)
                        .lineLimit(1)
                    Image(systemName: "chevron.up.chevron.down")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
            .accessibilityLabel("\(action.name) is \(assignment.display). Double tap to choose a different action.")
        }
    }
}

/// The categorized dropdown. One submenu per category, never one giant list.
struct OutputMenu<Label: View>: View {
    @Environment(AppModel.self) private var model
    let actionID: String
    @ViewBuilder let label: Label

    private var selected: OutputAction? { model.assignment(for: actionID).output }

    var body: some View {
        Menu {
            Button {
                set(nil)
            } label: {
                if selected == nil {
                    SwiftUI.Label("Unassigned", systemImage: "checkmark")
                } else {
                    Text("Unassigned")
                }
            }
            ForEach(OutputCategory.allCases, id: \.self) { category in
                Menu(category.rawValue) {
                    ForEach(QuadStickCatalog.outputs.filter { $0.category == category }) { output in
                        Button {
                            set(output)
                        } label: {
                            if selected?.id == output.id {
                                SwiftUI.Label(output.name, systemImage: "checkmark")
                            } else {
                                Text(output.name)
                            }
                        }
                    }
                }
            }
        } label: {
            label
        }
        .buttonStyle(.borderless)
    }

    private func set(_ output: OutputAction?) {
        var a = model.assignment(for: actionID)
        a.output = output
        model.setAssignment(a, for: actionID)
    }
}

/// Behavior and naming for one action. Assignment itself is the dropdown.
struct ActionEditorView: View {
    @Environment(AppModel.self) private var model
    let action: InputActionDef

    private var assignment: Assignment { model.assignment(for: action.id) }

    var body: some View {
        List {
            Section {
                HStack {
                    Text("Action")
                    Spacer()
                    OutputMenu(actionID: action.id) {
                        HStack(spacing: 4) {
                            Text(assignment.output?.name ?? "Unassigned")
                                .foregroundStyle(assignment.output == nil ? .secondary : Theme.accent)
                            Image(systemName: "chevron.up.chevron.down")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
                    }
                    .accessibilityLabel("Action: \(assignment.output?.name ?? "Unassigned"). Double tap to choose.")
                }
                if assignment.output != nil {
                    TextField("What you call it (optional)", text: labelBinding)
                        .accessibilityLabel("Your name for this action, for example Jump")
                }
            } header: {
                Text("What it does")
            } footer: {
                if assignment.output != nil {
                    Text("Your name shows next to the button, for example \u{201C}Jump (A)\u{201D}.")
                }
            }

            if assignment.output != nil {
                behaviorSection
            }

            if assignment.output != nil || assignment.function != nil {
                Section {
                    Button("Remove assignment", role: .destructive) {
                        model.setAssignment(Assignment(), for: action.id)
                    }
                }
            }
        }
        .navigationTitle(action.fullName)
        .navigationBarTitleDisplayMode(.inline)
    }

    private var labelBinding: Binding<String> {
        Binding(
            get: { assignment.label ?? "" },
            set: { text in
                var a = assignment
                a.label = text.isEmpty ? nil : text
                model.setAssignment(a, for: action.id)
            }
        )
    }

    private var behaviorSection: some View {
        Section {
            Picker("Behavior", selection: functionKindBinding) {
                ForEach(FunctionKind.allCases) { Text($0.rawValue).tag($0) }
            }
            FunctionParameterEditor(actionID: action.id)
        } header: {
            Text("How it behaves")
        } footer: {
            Text(assignment.function?.explanation
                 ?? "The action is pressed while the input is active, like a normal button.")
        }
    }

    private var functionKindBinding: Binding<FunctionKind> {
        Binding(
            get: { FunctionKind(assignment.function) },
            set: { kind in
                var a = assignment
                a.function = kind.defaultFunction(previous: a.function)
                model.setAssignment(a, for: action.id)
            }
        )
    }
}

enum FunctionKind: String, CaseIterable, Identifiable {
    case none = "Normal"
    case toggle = "Toggle"
    case repeatHeld = "Repeat"
    case greaterThan = "Greater Than"
    case delayedLatch = "Delayed Latch"

    var id: String { rawValue }

    init(_ f: InputFunction?) {
        switch f {
        case nil: self = .none
        case .toggle: self = .toggle
        case .repeatWhileHeld: self = .repeatHeld
        case .greaterThan: self = .greaterThan
        case .delayedLatch: self = .delayedLatch
        }
    }

    func defaultFunction(previous: InputFunction?) -> InputFunction? {
        if FunctionKind(previous) == self { return previous }
        switch self {
        case .none: return nil
        case .toggle: return .toggle
        case .repeatHeld: return .repeatWhileHeld(intervalMS: 200)
        case .greaterThan: return .greaterThan(percent: 25)
        case .delayedLatch: return .delayedLatch(delayMS: 1000)
        }
    }
}

/// Sliders make impossible values impossible to enter. Exact milliseconds
/// live under Advanced.
struct FunctionParameterEditor: View {
    @Environment(AppModel.self) private var model
    let actionID: String
    @State private var showAdvanced = false

    var body: some View {
        switch model.assignment(for: actionID).function {
        case .repeatWhileHeld(let ms):
            timeEditor(title: "Repeat every", ms: ms, range: 50...2000) {
                .repeatWhileHeld(intervalMS: $0)
            }
        case .delayedLatch(let ms):
            timeEditor(title: "Wait before activating", ms: ms, range: 0...5000) {
                .delayedLatch(delayMS: $0)
            }
        case .greaterThan(let pct):
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text("Activation point")
                    Spacer()
                    Text("\(pct)%").foregroundStyle(.secondary).monospacedDigit()
                }
                Slider(value: percentBinding(pct), in: 0...100, step: 1) {
                    Text("Activation point")
                } minimumValueLabel: {
                    Text("0%").font(.caption2)
                } maximumValueLabel: {
                    Text("100%").font(.caption2)
                }
                .accessibilityValue("\(pct) percent")
            }
        case .toggle, nil:
            EmptyView()
        }
    }

    @ViewBuilder
    private func timeEditor(title: String, ms: Int, range: ClosedRange<Double>,
                            make: @escaping (Int) -> InputFunction) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title)
                Spacer()
                Text(InputFunction.seconds(ms)).foregroundStyle(.secondary).monospacedDigit()
            }
            Slider(value: msBinding(ms, make: make), in: range, step: 50) {
                Text(title)
            } minimumValueLabel: {
                Text(InputFunction.seconds(Int(range.lowerBound))).font(.caption2)
            } maximumValueLabel: {
                Text(InputFunction.seconds(Int(range.upperBound))).font(.caption2)
            }
            .accessibilityValue(InputFunction.seconds(ms))
        }
        DisclosureGroup("Advanced", isExpanded: $showAdvanced) {
            HStack {
                Text("Exact milliseconds")
                Spacer()
                TextField("ms", value: msFieldBinding(ms, make: make), format: .number)
                    .keyboardType(.numberPad)
                    .multilineTextAlignment(.trailing)
                    .frame(width: 90)
                    .accessibilityLabel("Exact milliseconds")
            }
        }
    }

    private func set(_ f: InputFunction) {
        var a = model.assignment(for: actionID)
        a.function = f
        model.setAssignment(a, for: actionID)
    }

    private func msBinding(_ ms: Int, make: @escaping (Int) -> InputFunction) -> Binding<Double> {
        Binding(get: { Double(ms) }, set: { set(make(Int($0))) })
    }

    private func msFieldBinding(_ ms: Int, make: @escaping (Int) -> InputFunction) -> Binding<Int> {
        Binding(get: { ms }, set: { set(make($0)) })
    }

    private func percentBinding(_ pct: Int) -> Binding<Double> {
        Binding(get: { Double(pct) }, set: { set(.greaterThan(percent: Int($0))) })
    }
}
