import RevenueCat
import SwiftUI

/// The tip jar, at the foot of the home screen.
///
/// Three consumables and nothing behind them: no entitlement, no unlock, the app
/// is identical afterwards. Editing, validation, import and install stay free
/// forever, so a tip can never be what somebody pays to operate their own
/// hardware. Ported from In My Voice's jar, same shape.
///
/// Prices come from the store, localised, never from this file.
@MainActor
private enum TipStore {
    private static var opened = false

    /// Configured the first time the jar draws, not at launch, so nothing talks
    /// to a server before the home screen is on show.
    static func open() {
        guard !opened else { return }
        opened = true
        // Public SDK key. It can read the catalogue and start a purchase Apple has
        // to approve, nothing more. StoreKit 2 so consumables finish themselves.
        Purchases.configure(with: .builder(withAPIKey: "appl_CTQvbwOcmNepQGeZXrUCDRBLuOP")
            .with(storeKitVersion: .storeKit2)
            .build())
    }
}

struct TipJar: View {
    @State private var tips: [Package] = []
    @State private var buying: String?
    @State private var thanked = false
    @State private var asked = false
    @State private var trouble: String?

    var body: some View {
        VStack(spacing: 8) {
            if thanked {
                Image(systemName: "heart.fill")
                    .font(.title2)
                    .foregroundStyle(Theme.accent)
                Text("Thank you.")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.white)
            } else {
                Image(systemName: "cup.and.saucer.fill")
                    .font(.title2)
                    .foregroundStyle(Theme.accent)
                VStack(spacing: 2) {
                    Text("SipStudio is free.")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(.white)
                    Text("Leave a tip if it helped. It unlocks nothing.")
                        .font(.footnote)
                        .foregroundStyle(Color(white: 0.65))
                }
                prices
                if let trouble {
                    Text(trouble).font(.caption2).foregroundStyle(.red)
                }
            }
        }
        .multilineTextAlignment(.center)
        .padding(.vertical, 20)
        .padding(.horizontal, 16)
        .frame(maxWidth: .infinity)
        .themedCard()
        .task { await load() }
    }

    /// Real buttons once the store answers, grey shapes of the same size until
    /// then, so nothing under the row jumps when prices land.
    @ViewBuilder
    private var prices: some View {
        if tips.isEmpty, asked {
            Text("The store is not answering right now.")
                .font(.footnote)
                .foregroundStyle(Color(white: 0.65))
            Button("Try again") { Task { await load(again: true) } }
                .font(.caption)
        } else if tips.isEmpty {
            HStack(spacing: 8) {
                ForEach(0..<3, id: \.self) { _ in
                    Text("$0.00")
                        .font(.subheadline.weight(.medium).monospacedDigit())
                        .frame(minWidth: 56)
                        .padding(.vertical, 7)
                        .padding(.horizontal, 12)
                        .background(Theme.accent.opacity(0.25), in: Capsule())
                }
            }
            .redacted(reason: .placeholder)
            .padding(.top, 2)
            .accessibilityLabel("Loading tip prices")
        } else {
            HStack(spacing: 8) {
                ForEach(tips, id: \.identifier) { tip in button(tip) }
            }
            .padding(.top, 2)
        }
    }

    private func button(_ tip: Package) -> some View {
        Button { buy(tip) } label: {
            if buying == tip.identifier {
                ProgressView().controlSize(.small)
            } else {
                Text(tip.localizedPriceString)
                    .font(.subheadline.weight(.medium).monospacedDigit())
                    .frame(minWidth: 56, minHeight: 30)
            }
        }
        .buttonStyle(.borderedProminent)
        .tint(Theme.accent)
        .disabled(buying != nil)
        .accessibilityLabel("\(tip.storeProduct.localizedTitle), \(tip.localizedPriceString)")
    }

    private func load(again: Bool = false) async {
        guard tips.isEmpty else { return }
        if again { asked = false }
        TipStore.open()
        do {
            let offering = try await Purchases.shared.offerings().current
            tips = (offering?.availablePackages ?? [])
                .sorted { $0.storeProduct.price < $1.storeProduct.price }
        } catch {
            tips = []
        }
        asked = true
    }

    private func buy(_ tip: Package) {
        buying = tip.identifier
        trouble = nil
        Task {
            do {
                let result = try await Purchases.shared.purchase(package: tip)
                // Cancelling is not an error. The sheet they dismissed was the message.
                if !result.userCancelled { thanked = true }
            } catch {
                trouble = error.localizedDescription
            }
            buying = nil
        }
    }
}
