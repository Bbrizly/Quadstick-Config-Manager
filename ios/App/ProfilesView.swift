import SwiftUI
import UIKit
import UniformTypeIdentifiers
import QuadStickKit

struct ProfilesView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    @State private var showSheetsImport = false
    @State private var showFileImporter = false
    @State private var badFileAlert = false
    @State private var importNotes: [String]?

    var body: some View {
        List {
            Section {
                ForEach(Array(model.profiles.enumerated()), id: \.element.id) { index, p in
                    profileRow(index: index, profile: p)
                }
                .onDelete(perform: model.profiles.count > 1 ? delete : nil)
            } footer: {
                Text("A profile is the complete setup for one game or activity. The QuadStick loads one profile at a time.")
            }
        }
        .navigationTitle("Profiles")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Menu("Add", systemImage: "plus") {
                    Button("New profile") { addNewProfile() }
                    Button("Duplicate current") { duplicateCurrent() }
                    Button("Paste Google Sheets link") { showSheetsImport = true }
                    Button("Import a file") { showFileImporter = true }
                }
            }
        }
        .sheet(isPresented: $showSheetsImport) {
            SheetsImportView()
        }
        .fileImporter(isPresented: $showFileImporter,
                      allowedContentTypes: [.commaSeparatedText, .plainText]) { result in
            handleFileImport(result)
        }
        .alert("That file does not look like a QuadStick profile.", isPresented: $badFileAlert) {
            Button("OK", role: .cancel) {}
        }
        .alert("Imported with notes", isPresented: notesPresented) {
            Button("OK", role: .cancel) {}
        } message: {
            Text((importNotes ?? []).joined(separator: "\n"))
        }
    }

    private var notesPresented: Binding<Bool> {
        Binding(get: { importNotes != nil }, set: { if !$0 { importNotes = nil } })
    }

    private func profileRow(index: Int, profile: Profile) -> some View {
        let selected = index == model.profileIndex
        return Button {
            model.selectProfile(index)
            dismiss()
        } label: {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    // Concrete colours: the row Button would tint its label.
                    Text(profile.name).font(.headline).foregroundStyle(.white)
                    Text("\(profile.modes.count) mode\(profile.modes.count == 1 ? "" : "s") \u{00B7} \(profile.controllerType.rawValue)")
                        .font(.caption)
                        .foregroundStyle(Color(white: 0.65))
                }
                Spacer()
                if selected {
                    Image(systemName: "checkmark")
                        .foregroundStyle(Theme.accent)
                        .accessibilityLabel("Selected")
                }
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(profile.name), \(profile.modes.count) modes, \(profile.controllerType.rawValue)\(selected ? ", selected" : "")")
    }

    private func delete(at offsets: IndexSet) {
        guard model.profiles.count > 1 else { return }
        for index in offsets {
            model.deleteProfile(at: index)
        }
    }

    private func addNewProfile() {
        model.addProfile(Profile(name: "New Profile", controllerType: .standard,
                                  modes: [Mode(name: "Mode 1", assignments: [:])]))
    }

    private func duplicateCurrent() {
        let source = model.profile
        let modes = source.modes.map { Mode(name: $0.name, assignments: $0.assignments) }
        model.addProfile(Profile(name: "\(source.name) copy", controllerType: source.controllerType, modes: modes))
    }

    private func handleFileImport(_ result: Result<URL, Error>) {
        guard case .success(let url) = result else { return }
        let didAccess = url.startAccessingSecurityScopedResource()
        defer { if didAccess { url.stopAccessingSecurityScopedResource() } }
        guard let data = try? Data(contentsOf: url) else {
            badFileAlert = true
            return
        }
        let csv = String(decoding: data, as: UTF8.self)
        let fallbackName = url.deletingPathExtension().lastPathComponent
        guard let result = DeviceFile.importProfile(csv: csv, fallbackName: fallbackName) else {
            badFileAlert = true
            return
        }
        model.addProfile(result.profile)
        if !result.notes.isEmpty {
            importNotes = result.notes
        }
    }
}

/// Sheet for pulling a profile out of a shared Google Sheets link.
private struct SheetsImportView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    @State private var link = ""
    @State private var isLoading = false
    @State private var errorText: String?
    @State private var importNotes: [String]?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Text("Paste a link to a QuadStick profile sheet. The sheet must be shared so anyone with the link can view it.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)

                    TextField("Google Sheets link", text: $link)
                        .keyboardType(.URL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .accessibilityLabel("Google Sheets link")

                    HStack {
                        Button("Paste") {
                            link = UIPasteboard.general.string ?? link
                        }
                        Spacer()
                        if isLoading {
                            ProgressView()
                        }
                        Button("Import") {
                            importFromSheets()
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(link.trimmingCharacters(in: .whitespaces).isEmpty || isLoading)
                    }

                    if let errorText {
                        Label(errorText, systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                            .font(.footnote)
                    }
                } footer: {
                    Text("A Google Sheets link gives one tab, which is one mode. Import a .csv file to get every mode.")
                }
            }
            .navigationTitle("Import from Google Sheets")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
            .alert("Imported with notes", isPresented: notesPresented) {
                Button("OK") { dismiss() }
            } message: {
                Text((importNotes ?? []).joined(separator: "\n"))
            }
        }
    }

    private var notesPresented: Binding<Bool> {
        Binding(get: { importNotes != nil }, set: { if !$0 { importNotes = nil } })
    }

    private func importFromSheets() {
        errorText = nil
        guard let url = SheetsLink.csvExportURL(from: link) else {
            errorText = "That does not look like a Google Sheets link."
            return
        }
        isLoading = true
        Task {
            defer { isLoading = false }
            do {
                let (data, _) = try await URLSession.shared.data(from: url)
                let text = String(decoding: data, as: UTF8.self)
                if text.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("<") {
                    errorText = "Google did not let us read the sheet. In Google Sheets, use Share and set General access to Anyone with the link."
                    return
                }
                guard let result = DeviceFile.importProfile(csv: text, fallbackName: "Imported profile") else {
                    errorText = "That sheet does not look like a QuadStick profile."
                    return
                }
                model.addProfile(result.profile)
                if result.notes.isEmpty {
                    dismiss()
                } else {
                    importNotes = result.notes
                }
            } catch {
                errorText = "Could not read that sheet (\(error.localizedDescription))."
            }
        }
    }
}
