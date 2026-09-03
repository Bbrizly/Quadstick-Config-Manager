import SwiftUI
import QuadStickKit

/// One physical input and the four things it can do. Tapping a row picks what
/// that action does, which is the change people come here to make. Behaviour
/// and naming sit behind their own labelled button, one tap further, so the
/// common case stays two taps from the device picture.
struct InputDetailView: View {
    @Environment(AppModel.self) private var model
    let input: DeviceInput

    var body: some View {
        List {
            if let detail = input.detail {
                Section { EmptyView() } footer: { Text(detail) }
            }
            Section {
                ForEach(input.actions) { action in
                    ActionRow(action: action)
                }
            } header: {
                Text("In \(model.mode.name) mode")
            } footer: {
                Text("Tap an action to choose its output, behavior, and name.")
            }
        }
        .navigationTitle(input.name)
        .navigationBarTitleDisplayMode(.large)
    }
}

private struct ActionRow: View {
    @Environment(AppModel.self) private var model
    let action: InputActionDef
    @State private var showPicker = false

    var body: some View {
        let assignment = model.assignment(for: action.id)
        HStack(spacing: 12) {
            Button {
                showPicker = true
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    Text(action.name)
                        .foregroundStyle(.primary)
                    Text(assignment.display)
                        .font(.subheadline)
                        .foregroundStyle(assignment.output == nil ? .secondary : Theme.accent)
                    if let f = assignment.function {
                        Text(f.summary)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .frame(minHeight: 44)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("\(action.name), \(assignment.display)")
            .accessibilityHint("Choose what this does")

            NavigationLink(value: action.id) {
                Image(systemName: "slider.horizontal.3")
                    .frame(width: 44, height: 44)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(Theme.accent)
            .labelsHidden()
            .frame(width: 44)
            .accessibilityLabel("Options for \(action.name)")
            .accessibilityHint("Set how it behaves and what you call it")
        }
        .sheet(isPresented: $showPicker) {
            OutputPicker(actionID: action.id)
        }
    }
}

/// Behavior and naming for one action. Assignment itself is the dropdown.
struct ActionEditorView: View {
    @Environment(AppModel.self) private var model
    let action: InputActionDef
    @State private var showPicker = false

    private var assignment: Assignment { model.assignment(for: action.id) }

    var body: some View {
        List {
            Section {
                Button {
                    showPicker = true
                } label: {
                    HStack {
                        Text("Action").foregroundStyle(.primary)
                        Spacer()
                        Text(assignment.output?.name ?? "Not set")
                            .foregroundStyle(assignment.output == nil ? .secondary : Theme.accent)
                        Image(systemName: "chevron.right")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                    }
                    .frame(minHeight: 44)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Action: \(assignment.output?.name ?? "Not set")")
                .accessibilityHint("Choose what this does")
                if assignment.output != nil {
                    TextField("What you call it (optional)", text: labelBinding)
                        .accessibilityLabel("Your name for this action, for example Jump")
                }
            } header: {
                Text("What it does")
            } footer: {
                if assignment.output != nil {
                    Text("Your name appears next to the button, such as \u{201C}Jump (A)\u{201D}.")
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
        .sheet(isPresented: $showPicker) {
            OutputPicker(actionID: action.id)
        }
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
