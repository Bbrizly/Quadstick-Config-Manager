import SwiftUI
import QuadStickKit

/// Google Drive for one profile: back it up, get a link to send, or pull one
/// back from a sheet.
///
/// The sheet is the shared copy. The desktop app writes the same shape to the
/// same place, so a profile backed up here opens there and the other way round,
/// and the link is the same link either app hands out.
struct DriveView: View {
    @Environment(AppModel.self) private var model
    @State private var shareURL: URL?
    @State private var notes: [String] = []
    @State private var showSheets = false
    @State private var recreateOffer = false

    private var drive: DriveAccount { model.drive }

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                statusCard

                switch drive.status {
                case .notConfigured:
                    notConfiguredCard
                case .signedOut:
                    signInCard
                default:
                    actions
                }

                if !notes.isEmpty { notesCard }
            }
            .padding()
            .frame(maxWidth: 640)
            .frame(maxWidth: .infinity)
        }
        .background(Theme.background)
        .navigationTitle("Google Drive")
        .navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $showSheets) { DriveSheetListView(notes: $notes) }
        .sheet(item: Binding(get: { shareURL.map(ShareItem.init) },
                             set: { if $0 == nil { shareURL = nil } })) { item in
            ActivityView(url: item.url)
        }
        .alert("Sheet edited somewhere else", isPresented: conflictShowing) {
            Button("Use this phone's copy") { model.drive.conflict?.decide(.replaceWithMine); model.drive.conflict = nil }
            Button("Use the online copy", role: .destructive) { model.drive.conflict?.decide(.keepOnline); model.drive.conflict = nil }
        } message: {
            Text("\(model.drive.conflict?.profileName ?? "This profile")'s sheet changed since this phone last wrote it. Using this phone's copy replaces the online one; Google keeps the old version in the sheet history. Using the online copy replaces the phone's copy.")
        }
        .alert("That sheet is gone", isPresented: $recreateOffer) {
            Button("Make a new sheet") { Task { await run { await drive.recreate(model.profile) } } }
            Button("Leave it", role: .cancel) {}
        } message: {
            Text("The Google Sheet for this profile is missing. It may have been deleted or moved to the trash. Your phone's copy is untouched.")
        }
    }

    private var conflictShowing: Binding<Bool> {
        Binding(get: { model.drive.conflict != nil },
                set: { if !$0 { model.drive.conflict = nil } })
    }

    // MARK: - What is going on

    @ViewBuilder
    private var statusCard: some View {
        switch drive.status {
        case .notConfigured:
            row("Not set up in this build", icon: "wrench.and.screwdriver", tint: .secondary)
        case .signedOut:
            row("Not signed in", icon: "person.crop.circle.badge.xmark", tint: .secondary)
        case .working(let message):
            HStack(spacing: 12) {
                ProgressView()
                Text(message)
                Spacer(minLength: 0)
            }
            .padding()
            .frame(maxWidth: .infinity, alignment: .leading)
            .themedCard()
            .accessibilityElement(children: .combine)
        case .signedIn:
            row(model.profile.sheetID == nil
                ? "Signed in. This profile is not backed up yet."
                : "Signed in. This profile is backed up.",
                icon: model.profile.sheetID == nil ? "checkmark.circle" : "checkmark.icloud",
                tint: .green)
        case .problem(let message):
            row(message, icon: "exclamationmark.triangle.fill", tint: .orange)
        }
    }

    private func row(_ text: String, icon: String, tint: Color) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: icon).foregroundStyle(tint).accessibilityHidden(true)
            Text(text)
            Spacer(minLength: 0)
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedCard()
        .accessibilityElement(children: .combine)
    }

    // MARK: - The three states

    private var notConfiguredCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Google Drive is off in this build. Profiles still save on this phone, and Install still puts them on the QuadStick.")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedCard()
    }

    private var signInCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Sign in with Google to back up profiles to Sheets and share them by link.")
            Label("This app only ever sees the sheets it made for you. It cannot read the rest of your Drive.",
                  systemImage: "lock.shield")
                .font(.footnote)
                .foregroundStyle(.secondary)
            Button("Sign in with Google") { Task { await drive.signIn() } }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .frame(maxWidth: .infinity)
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedCard()
    }

    private var actions: some View {
        VStack(spacing: 10) {
            Button {
                Task { await run { await drive.push(model.profile) } }
            } label: {
                actionRow(model.profile.sheetID == nil ? "Back up this profile" : "Update the backup",
                          detail: "Writes \(model.profile.name) to Google Sheets, one tab for each mode",
                          icon: "arrow.up.doc")
            }

            Button {
                Task {
                    if let (url, result) = await drive.shareLink(for: model.profile) {
                        notes = model.applyPush(result)
                        shareURL = url
                    } else { offerRecreateIfGone() }
                }
            } label: {
                actionRow("Share a link",
                          detail: "Anyone with the link can open a read-only copy",
                          icon: "square.and.arrow.up")
            }

            Button { showSheets = true } label: {
                actionRow("Open one from Drive",
                          detail: "Bring a profile back onto this phone",
                          icon: "square.and.arrow.down")
            }

            if let id = model.profile.sheetID {
                Link(destination: DriveClient.editURL(id)) {
                    actionRow("Open the sheet in Google",
                              detail: "Edit it in a browser, on any device",
                              icon: "safari")
                }
            }

            Button(role: .destructive) {
                Task { await drive.signOut() }
            } label: {
                Text("Sign out of Google")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .padding(.top, 6)
        }
    }

    private func actionRow(_ title: String, detail: String, icon: String) -> some View {
        HStack(spacing: 14) {
            Image(systemName: icon)
                .font(.title3)
                .frame(width: 32)
                .foregroundStyle(Theme.accent)
            VStack(alignment: .leading, spacing: 2) {
                // Concrete colours: a Button tints its label, and the
                // hierarchical styles resolve against that tint.
                Text(title).font(.headline).foregroundStyle(.white)
                Text(detail).font(.caption).foregroundStyle(Color(white: 0.65))
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 10)
        .padding(.horizontal, 14)
        .frame(minHeight: 44)
        .themedCard()
        .contentShape(Rectangle())
    }

    // An import that changed something says what it changed. A backup that
    // quietly dropped a mode is the one thing this must never do.
    private var notesCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("What changed").font(.headline)
            ForEach(notes, id: \.self) { note in
                Label(note, systemImage: "info.circle")
                    .font(.footnote)
            }
            Button("Got it") { notes = [] }
                .buttonStyle(.bordered)
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedCard()
    }

    private func run(_ work: () async -> PushResult?) async {
        if let result = await work() { notes = model.applyPush(result) }
        else { offerRecreateIfGone() }
    }

    // A missing sheet is the one failure with an obvious next step, so it is
    // offered rather than left as a sentence about a sheet that is not coming
    // back.
    private func offerRecreateIfGone() {
        if drive.lastFailureWasMissingSheet, model.profile.sheetID != nil {
            recreateOffer = true
        }
    }
}

private struct ShareItem: Identifiable {
    let url: URL
    var id: String { url.absoluteString }
}

/// The system share sheet, so the link can go wherever the person already talks
/// to people. Copy is one of the options in it.
private struct ActivityView: UIViewControllerRepresentable {
    let url: URL

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: [url], applicationActivities: nil)
    }

    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}

/// Every sheet this app made for this account, newest first.
private struct DriveSheetListView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Binding var notes: [String]

    @State private var sheets: [DriveSheetInfo] = []
    @State private var loading = true

    var body: some View {
        NavigationStack {
            Group {
                if loading {
                    ProgressView("Looking for your sheets...")
                } else if sheets.isEmpty {
                    ContentUnavailableView("No sheets yet",
                                           systemImage: "tray",
                                           description: Text("Back up a profile and it will show up here, on this phone and on the desktop app."))
                } else {
                    List(sheets) { sheet in
                        Button {
                            Task { await open(sheet) }
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(sheet.name).font(.headline).foregroundStyle(.white)
                                Text(Self.when(sheet.modifiedTime))
                                    .font(.caption)
                                    .foregroundStyle(Color(white: 0.65))
                            }
                        }
                        .accessibilityHint("Adds this profile to this phone")
                    }
                }
            }
            .navigationTitle("Your sheets")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { Button("Done") { dismiss() } }
            .task {
                sheets = await model.drive.mySheets() ?? []
                loading = false
            }
        }
    }

    private func open(_ sheet: DriveSheetInfo) async {
        guard let csv = await model.drive.download(sheet.id) else { return }
        guard let imported = DeviceFile.importProfile(csv: csv, fallbackName: sheet.name) else {
            notes = ["\(sheet.name) did not contain a profile this app could read."]
            dismiss()
            return
        }
        var profile = imported.profile
        profile.sheetID = sheet.id
        profile.sheetSyncedTime = sheet.modifiedTime
        model.addProfile(profile)
        notes = imported.notes
        dismiss()
    }

    // Drive gives an RFC 3339 stamp. Nobody reads those.
    static func when(_ iso: String) -> String {
        guard let date = ISO8601DateFormatter().date(from: iso) else { return "Last changed at an unknown time" }
        let style = RelativeDateTimeFormatter()
        style.unitsStyle = .full
        return "Changed \(style.localizedString(for: date, relativeTo: Date()))"
    }
}
