import SwiftUI
import QuadStickKit

struct ValidationListView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 12) {
                    ForEach(model.issues) { IssueCard(issue: $0) }
                }
                .padding()
            }
            .navigationTitle("Problems")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }
}

/// What is wrong, where it is, how to fix it. Severity is shown by word and
/// icon, never by colour alone.
struct IssueCard: View {
    let issue: Issue

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: issue.severity == .error ? "exclamationmark.octagon.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(issue.severity == .error ? .red : .orange)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(issue.severity == .error ? "Error" : "Warning")
                    .font(.caption.bold())
                    .foregroundStyle(issue.severity == .error ? .red : .orange)
                Text(issue.message)
                Text(issue.location)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(issue.fix)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.card))
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(issue.severity == .error ? "Error" : "Warning"). \(issue.message) Location: \(issue.location). \(issue.fix)")
    }
}
