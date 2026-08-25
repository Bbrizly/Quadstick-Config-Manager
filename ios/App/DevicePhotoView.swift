import SwiftUI
import QuadStickKit

/// The device photo is the map: every part of the front is a ring you can tap.
/// The list underneath says the same thing in words and always has full-size
/// rows, so nobody depends on seeing the picture or hitting a small target.
/// This is the image-map pattern with its text equivalent, not decoration.
struct DevicePhotoView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 16) {
            photo
            partList
        }
    }

    // MARK: - The picture

    private var photo: some View {
        Image("QuadStickFront")
            .resizable()
            .scaledToFit()
            .overlay {
                GeometryReader { geo in
                    ForEach(DevicePhoto.hotspots) { spot in
                        if let input = model.capabilities.input(spot.inputID) {
                            region(input, at: spot, in: geo.size)
                        }
                    }
                }
            }
            .aspectRatio(DevicePhoto.aspectRatio, contentMode: .fit)
            // The photo repeats what the list below says, so VoiceOver reads
            // the list instead of every ring a second time.
            .accessibilityHidden(true)
    }

    private func region(_ input: DeviceInput, at spot: DeviceHotspot, in size: CGSize) -> some View {
        // Rings stay inside the gap between the two closest parts, so one
        // part's target never sits on top of another's. KitTests pins it.
        let ring = size.width * 0.09
        let tap = size.width * DevicePhoto.minimumSeparation
        return RegionRing(assigned: assignedCount(input), total: input.actions.count,
                          size: ring, name: spot.shortName)
            .frame(width: tap, height: tap)
            .contentShape(Circle())
            .position(x: spot.x * size.width, y: spot.y * size.height)
            .overlay {
                NavigationLink(value: input) { Color.clear }
                    .frame(width: tap, height: tap)
                    .position(x: spot.x * size.width, y: spot.y * size.height)
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
