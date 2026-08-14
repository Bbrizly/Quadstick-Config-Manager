import SwiftUI

/// One place for the look: near-black ground, dark cards, bold orange accent.
/// The app commits to dark on purpose, so every colour is explicit.
enum Theme {
    static let accent = Color(red: 1.0, green: 0.45, blue: 0.08)
    static let background = Color(red: 0.05, green: 0.05, blue: 0.06)
    static let card = Color(red: 0.11, green: 0.11, blue: 0.13)
    static let cardRaised = Color(red: 0.16, green: 0.16, blue: 0.18)
    static let deviceBody = Color(red: 0.13, green: 0.13, blue: 0.15)
}

extension View {
    func themedCard(cornerRadius: CGFloat = 12) -> some View {
        background(RoundedRectangle(cornerRadius: cornerRadius).fill(Theme.card))
    }
}
