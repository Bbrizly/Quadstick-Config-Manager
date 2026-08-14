import SwiftUI
import UIKit
import UniformTypeIdentifiers
import QuadStickKit

/// Puts the current profile onto the QuadStick's USB drive.
struct InstallView: View {
    @Environment(AppModel.self) private var model

    @State private var makeDefault = true
    @State private var showExporter = false
    @State private var exportDocument: CSVDocument?
    @State private var resultText: String?
    @State private var resultIsError = false

    private var idiomName: String {
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
    }

    private var errorCount: Int {
        model.issues.filter { $0.severity == .error }.count
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                if errorCount > 0 {
                    warningCard
                }

                Toggle(isOn: $makeDefault) {
                    Text("Make it the startup profile")
                }
                .padding()
                .themedCard()

                Text("On: saves as default.csv, which the QuadStick loads every time it starts, and it switches over a few seconds after you save. Off: saves under its own name, and you pick it on the QuadStick later.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)

                stepsCard

                Button("Save to file") {
                    exportDocument = CSVDocument(text: DeviceFile.export(model.profile, makeDefault: makeDefault))
                    showExporter = true
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .frame(maxWidth: .infinity)

                if let resultText {
                    Label(resultText, systemImage: resultIsError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                        .foregroundStyle(resultIsError ? .orange : .green)
                        .padding()
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .themedCard()
                }

                Text("Works with iPhones and iPads with a USB-C port. On older Lightning devices, use a computer instead.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            .padding()
        }
        .background(Theme.background)
        .navigationTitle("Install \(model.profile.name)")
        .navigationBarTitleDisplayMode(.inline)
        .fileExporter(isPresented: $showExporter,
                      document: exportDocument,
                      contentType: .commaSeparatedText,
                      defaultFilename: makeDefault ? "default" : DeviceFile.sanitizedFileName(model.profile.name)) { result in
            handleExportResult(result)
        }
    }

    private var warningCard: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "exclamationmark.octagon.fill")
                .foregroundStyle(.red)
                .accessibilityHidden(true)
            Text("This profile has \(errorCount) error\(errorCount == 1 ? "" : "s"). It may not work on the QuadStick until they are fixed.")
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedCard()
    }

    private var stepsCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            stepRow(1, "Plug the QuadStick into this \(idiomName) with a USB-C cable.")
            stepRow(2, "Tap Save to file below.")
            stepRow(3, "In the save screen, choose the QuadStick drive and tap Save.")
            stepRow(4, "Replace the existing file if it asks.")
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedCard()
    }

    private func stepRow(_ number: Int, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Text("\(number)")
                .font(.caption.bold())
                .frame(width: 24, height: 24)
                .background(Circle().fill(Theme.cardRaised))
                .accessibilityHidden(true)
            Text(text)
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Step \(number). \(text)")
    }

    private func handleExportResult(_ result: Result<URL, Error>) {
        switch result {
        case .success:
            resultIsError = false
            resultText = makeDefault
                ? "Saved. The QuadStick reloads on its own within a few seconds."
                : "Saved. Select the profile on the QuadStick to use it."
        case .failure(let error):
            resultIsError = true
            resultText = error.localizedDescription
        }
    }
}

/// Wraps the exported CSV text so .fileExporter can write it.
struct CSVDocument: FileDocument {
    static let readableContentTypes: [UTType] = [.commaSeparatedText]

    var text: String

    init(text: String) {
        self.text = text
    }

    init(configuration: ReadConfiguration) throws {
        let data = configuration.file.regularFileContents ?? Data()
        text = String(decoding: data, as: UTF8.self)
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: Data(text.utf8))
    }
}
