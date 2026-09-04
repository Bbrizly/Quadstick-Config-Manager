import SwiftUI
import UIKit
import QuadStickKit

/// The button someone is about to press, beside the words that already say
/// it. Ported from src/QuadStick.App/OutputVisuals.cs: real Xelu art when the
/// resolver finds a file for it, a small drawn shape when it does not. Always
/// decorative, so the text label carries the meaning on its own.
struct OutputGlyph: View {
    let action: OutputAction?
    let promptStyle: ControllerPromptStyle
    @ScaledMetric(relativeTo: .body) private var size: CGFloat = 28

    var body: some View {
        // A floor, not a fixed box: a keycap plate widens for its word, but
        // "not set" and every other glyph still line up on this minimum.
        Group {
            if let action {
                content(for: OutputVisual.for(action, promptStyle: promptStyle))
            }
        }
        .frame(minWidth: size, minHeight: size)
        .accessibilityHidden(true)
    }

    @ViewBuilder
    private func content(for visual: OutputVisual) -> some View {
        // The C# wraps its bitmap load in a try and falls back to the drawn
        // shape on failure. UIImage(named:) is that same check here: a
        // missing imageset must never leave a blank hole beside the label.
        if let key = visual.assetKey, let art = OutputVisual.assetPath(for: key),
           UIImage(named: art.name) != nil {
            Image(art.name)
                .resizable()
                .scaledToFit()
                .rotationEffect(.degrees(art.rotation))
                .frame(width: size, height: size)
        } else {
            DrawnGlyph(visual: visual, size: size)
        }
    }
}

/// The shapes for output kinds Xelu's pack does not cover: keyboard, mouse,
/// shoulder buttons, analog stick movement, and the neutral fallback.
private struct DrawnGlyph: View {
    let visual: OutputVisual
    let size: CGFloat

    var body: some View {
        switch visual.kind {
        case .keycap:
            Plate(text: visual.keycapText ?? "?", size: size)
        case .mouse:
            // No mouse art and no symbol from the resolver, so a word would
            // just be whatever's left over from the row's own label, cut off
            // in a box this small. A plain mouse mark reads at any width.
            Marked(content: .icon("computermouse.fill"), kind: .roundedSquare, size: size)
        case .dPad:
            Marked(content: .symbol(visual.symbol ?? "?"), kind: .roundedSquare, size: size)
        case .joystick:
            Marked(content: .symbol(visual.symbol ?? (visual.stickSide == .right ? "R" : "L")),
                   kind: .circle, size: size)
        case .shoulder:
            Marked(content: .symbol(visual.symbol ?? "?"), kind: .capsule, size: size)
        case .faceButton, .generic:
            Plate(text: "?", size: size)
        }
    }
}

/// A rounded plate that grows to fit its word, the way a real keycap does.
/// Used for a generated keycap and the "?" fallback nothing else resolved to.
private struct Plate: View {
    let text: String
    let size: CGFloat

    var body: some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .foregroundStyle(.white)
            .lineLimit(1)
            .padding(.horizontal, 6)
            .frame(minWidth: size, minHeight: size)
            .background(RoundedRectangle(cornerRadius: 6).fill(Theme.cardRaised))
            .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color(white: 0.5), lineWidth: 1))
            // Sized to the word, not squeezed to a box: a wide keycap like
            // "Page Down" must never truncate into something unreadable.
            .fixedSize(horizontal: true, vertical: false)
    }
}

/// A fixed-size shape carrying a hardware marking or a pictogram: a rounded
/// square for the d-pad and mouse, a capsule for a shoulder button, a circle
/// for a stick well.
private struct Marked: View {
    enum Content { case symbol(String), icon(String) }
    enum Kind { case roundedSquare, capsule, circle }

    let content: Content
    let kind: Kind
    let size: CGFloat

    var body: some View {
        outline
            .frame(width: size, height: size)
            .overlay {
                switch content {
                case .symbol(let text):
                    Text(text)
                        .font(.footnote.weight(.bold))
                        .foregroundStyle(.white)
                        .minimumScaleFactor(0.6)
                        .lineLimit(1)
                        .padding(2)
                case .icon(let name):
                    Image(systemName: name)
                        .foregroundStyle(.white)
                        .font(.system(size: size * 0.5))
                }
            }
    }

    @ViewBuilder
    private var outline: some View {
        switch kind {
        case .roundedSquare:
            RoundedRectangle(cornerRadius: 6)
                .fill(Theme.cardRaised)
                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color(white: 0.5), lineWidth: 1))
        case .capsule:
            Capsule()
                .fill(Theme.cardRaised)
                .overlay(Capsule().strokeBorder(Color(white: 0.5), lineWidth: 1))
        case .circle:
            Circle()
                .fill(Theme.cardRaised)
                .overlay(Circle().strokeBorder(Color(white: 0.5), lineWidth: 1))
        }
    }
}
