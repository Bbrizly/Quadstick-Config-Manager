import SwiftUI
import QuadStickKit

/// Walks the current mode one physical input at a time. Not a game, just a
/// clear read-back of what was built.
struct ReviewView: View {
    @Environment(AppModel.self) private var model
    @State private var page = 0

    private var inputs: [DeviceInput] {
        model.capabilities.inputs.filter { input in
            input.face == .front
                || model.summary(of: input).contains { $0.assignment.output != nil }
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            TabView(selection: $page) {
                ForEach(Array(inputs.enumerated()), id: \.element.id) { index, input in
                    inputPage(input).tag(index)
                }
                summaryPage.tag(inputs.count)
            }
            .tabViewStyle(.page(indexDisplayMode: .never))

            HStack {
                Button("Back") { page = max(0, page - 1) }
                    .disabled(page == 0)
                Spacer()
                Text(page < inputs.count ? "\(page + 1) of \(inputs.count)" : "Done")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                if page < inputs.count {
                    Button("Looks good") { page += 1 }
                        .buttonStyle(.borderedProminent)
                }
            }
            .padding()
        }
        .navigationTitle("Review Controls · \(model.mode.name)")
        .navigationBarTitleDisplayMode(.inline)
    }

    private func inputPage(_ input: DeviceInput) -> some View {
        let rows = model.summary(of: input)
        return ScrollView {
            VStack(spacing: 16) {
                Text(input.name)
                    .font(.largeTitle.bold())
                    .padding(.top, 24)
                if let detail = input.detail {
                    Text(detail).font(.footnote).foregroundStyle(.secondary)
                }
                VStack(spacing: 0) {
                    ForEach(rows, id: \.action.id) { row in
                        HStack {
                            Text(row.action.name)
                            Spacer()
                            Text(row.assignment.display)
                                .foregroundStyle(row.assignment.output == nil ? .tertiary : .secondary)
                        }
                        .padding(.vertical, 12)
                        .padding(.horizontal, 16)
                        .accessibilityElement(children: .combine)
                        if row.action.id != rows.last?.action.id {
                            Divider().padding(.leading, 16)
                        }
                    }
                }
                .background(RoundedRectangle(cornerRadius: 12).fill(Theme.card))
                .padding(.horizontal)

                NavigationLink(value: input) {
                    Text("Change")
                }
                .buttonStyle(.bordered)
            }
        }
    }

    private var summaryPage: some View {
        let issues = model.issues
        let errors = issues.filter { $0.severity == .error }
        return ScrollView {
            VStack(spacing: 16) {
                Image(systemName: errors.isEmpty ? "checkmark.circle" : "exclamationmark.octagon")
                    .font(.system(size: 56))
                    .foregroundStyle(errors.isEmpty ? .green : .red)
                    .padding(.top, 32)
                    .accessibilityHidden(true)
                Text(errors.isEmpty ? "This configuration can work" : "This configuration has problems")
                    .font(.title2.bold())
                if issues.isEmpty {
                    Text("Every check passed. Nothing unusual was found.")
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(issues) { issue in
                        IssueCard(issue: issue)
                    }
                    .padding(.horizontal)
                }
            }
        }
    }
}
