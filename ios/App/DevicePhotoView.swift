import SwiftUI
import QuadStickKit

/// The device photo is the map: every part of the front is a ring you can tap.
/// The list underneath says the same thing in words and always has full-size
/// rows, so nobody depends on seeing the picture or hitting a small target.
/// This is the image-map pattern with its text equivalent, not decoration.
struct DevicePhotoView: View {
    @Environment(AppModel.self) private var model

    private var photo: DevicePhoto { DevicePhoto.for(model.settings.deviceModel) }

    // Parts this profile maps that the chosen model does not have. Never
    // hidden, never dropped: named in a banner and kept as ordinary reachable
    // rows below, the same rule the desktop diagram follows.
    private var foreignInputs: [DeviceInput] {
        QuadStickCatalog.inputsNotOn(model.settings.deviceModel, mappedBy: model.profile)
    }

    var body: some View {
        VStack(spacing: 16) {
            if !foreignInputs.isEmpty {
                mismatchBanner
            }
            photoBox
            partList
            if !foreignInputs.isEmpty {
                offModelSection
            }
        }
    }

    // MARK: - The picture

    private var photoBox: some View {
        GeometryReader { geo in
            ZStack {
                devicePicture(in: geo.size)
                ForEach(photo.hotspots) { spot in
                    if let input = model.capabilities.input(spot.inputID) {
                        region(input, at: spot, in: geo.size)
                    }
                }
            }
        }
        .aspectRatio(photo.aspectRatio, contentMode: .fit)
        // The photo repeats what the list below says, so VoiceOver reads
        // the list instead of every ring a second time.
        .accessibilityHidden(true)
    }

    /// Draws the file at its full native size, offset so the measured crop
    /// lands exactly inside `size`, then clips to it. The Singleton's file is
    /// 2048x2048 with wide transparent margins outside that crop; drawing it
    /// whole would shrink the device to half size. Matches DeviceDiagram.cs's
    /// FullSize/FullOffset. sourceX/Y/Width/Height are 0/0/1/1 for the FPS
    /// and Original, so this is a no-op offset for them.
    private func devicePicture(in size: CGSize) -> some View {
        let fullWidth = size.width / photo.sourceWidth
        let fullHeight = size.height / photo.sourceHeight
        return Image(photo.assetName)
            .resizable()
            .frame(width: fullWidth, height: fullHeight)
            .offset(x: -photo.sourceX * fullWidth, y: -photo.sourceY * fullHeight)
            .frame(width: size.width, height: size.height, alignment: .topLeading)
            .clipped()
    }

    private func region(_ input: DeviceInput, at spot: DeviceHotspot, in size: CGSize) -> some View {
        let pos = photo.position(of: spot)
        // Rings stay inside the gap between the two closest parts, so one
        // part's target never sits on top of another's. On the FPS the lip
        // and centre tube sit only 0.089 apart, tighter than a fixed 0.09
        // ring, so the drawn ring is capped at whichever is smaller on any
        // photo. The tap target keeps the full gap.
        let ring = size.width * min(0.09, photo.minimumSeparation)
        let tap = size.width * photo.minimumSeparation
        return RegionRing(assigned: assignedCount(input), total: input.actions.count,
                          size: ring, name: spot.shortName)
            .frame(width: tap, height: tap)
            .contentShape(Circle())
            .position(x: pos.x * size.width, y: pos.y * size.height)
            .overlay {
                NavigationLink(value: input) { Color.clear }
                    .frame(width: tap, height: tap)
                    .position(x: pos.x * size.width, y: pos.y * size.height)
            }
    }

    // MARK: - The same parts, in words

    private var partList: some View {
        VStack(spacing: 8) {
            ForEach(model.capabilities.inputs.filter { $0.face == .front }) { input in
                NavigationLink(value: input) {
                    PartRow(input: input)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(model.voiceOverSummary(of: input))
                .accessibilityHint("Opens this part")
            }
        }
    }

    private func assignedCount(_ input: DeviceInput) -> Int {
        input.actions.filter { model.assignment(for: $0.id).output != nil }.count
    }

    // MARK: - Parts the chosen model does not have

    private var mismatchBanner: some View {
        Label {
            Text(mismatchText)
                .font(.footnote)
        } icon: {
            Image(systemName: "exclamationmark.triangle")
        }
        .foregroundStyle(.orange)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    private var mismatchText: String {
        let names = foreignInputs.map(\.name).joined(separator: ", ")
        return "This profile maps parts your \(model.settings.deviceModel.displayName) does not have: \(names)."
    }

    private var offModelSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Mapped, but not on your \(model.settings.deviceModel.displayName)")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.secondary)
            VStack(spacing: 8) {
                ForEach(foreignInputs) { input in
                    NavigationLink(value: input) {
                        PartRow(input: input)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(model.voiceOverSummary(of: input))
                    .accessibilityHint("Opens this part")
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A tappable part of the photo. Assigned parts are a solid ring with the
/// number set; unassigned ones are a dashed ring. The shape carries the state,
/// never the colour on its own.
private struct RegionRing: View {
    let assigned: Int
    let total: Int
    let size: CGFloat
    let name: String

    var body: some View {
        ZStack {
            Circle()
                .fill(Theme.accent.opacity(assigned > 0 ? 0.22 : 0.10))
            Circle()
                .strokeBorder(Theme.accent,
                              style: StrokeStyle(lineWidth: 2.5,
                                                 dash: assigned > 0 ? [] : [4, 4]))
            if assigned > 0 {
                Text("\(assigned)")
                    .font(.system(size: max(10, size * 0.42), weight: .bold, design: .rounded))
                    .foregroundStyle(.white)
                    .shadow(color: .black.opacity(0.8), radius: 2)
            }
        }
        .frame(width: size, height: size)
        // A white halo keeps the ring readable over both the black case and
        // the white mouthpiece.
        .shadow(color: .black.opacity(0.55), radius: 3)
        // The number is a count, not a name. Without this the ring on the
        // gimbal arch and the ring on the lip disc are two orange circles in
        // the same corner of the photo and tapping one is a guess. The desktop
        // runs a leader line out to a named pill; there is no room for that
        // here, so the name sits under the ring.
        .overlay(alignment: .top) {
            Text(name)
                .font(.system(size: max(8, size * 0.30), weight: .semibold))
                .foregroundStyle(.white)
                .shadow(color: .black, radius: 2)
                .shadow(color: .black, radius: 3)
                .fixedSize()
                .offset(y: size * 0.62)
        }
    }
}

/// One part of the device, in words. Both faces use this, so a jack on the
/// back reads the same way as a tube on the front. Full width, full height, no
/// truncation of the part name at any text size.
struct PartRow: View {
    @Environment(AppModel.self) private var model
    let input: DeviceInput
    // The badge holds text, so it grows with the text. Fixed at 30 it clipped
    // the number at accessibility sizes.
    @ScaledMetric(relativeTo: .footnote) private var badge: CGFloat = 30

    private var assigned: Int {
        input.actions.filter { model.assignment(for: $0.id).output != nil }.count
    }

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                Circle()
                    .strokeBorder(Theme.accent,
                                  style: StrokeStyle(lineWidth: 2,
                                                     dash: assigned > 0 ? [] : [3, 3]))
                    .background(Circle().fill(Theme.accent.opacity(assigned > 0 ? 0.22 : 0.08)))
                if assigned > 0 {
                    Text("\(assigned)")
                        .font(.footnote.bold().monospacedDigit())
                        .foregroundStyle(.white)
                }
            }
            .frame(width: badge, height: badge)
            .fixedSize()

            VStack(alignment: .leading, spacing: 2) {
                Text(input.name)
                    .font(.body.weight(.medium))
                    .foregroundStyle(.white)
                Text(assigned == 0
                     ? "Nothing set"
                     : "\(assigned) of \(input.actions.count) set")
                    .font(.caption)
                    .foregroundStyle(Color(white: 0.65))
                if let detail = input.detail {
                    Text(detail)
                        .font(.caption2)
                        .foregroundStyle(Color(white: 0.5))
                }
            }
            Spacer(minLength: 0)
            Image(systemName: "chevron.right")
                .font(.caption)
                .foregroundStyle(.tertiary)
        }
        .padding(.vertical, 10)
        .padding(.horizontal, 14)
        .frame(minHeight: 44)
        .themedCard()
        .contentShape(Rectangle())
    }
}
