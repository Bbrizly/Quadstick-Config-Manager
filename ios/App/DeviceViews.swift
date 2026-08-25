import SwiftUI
import QuadStickKit

/// The back of the device. The front is the photo, in DevicePhotoView.

struct DeviceBackView: View {
    @Environment(AppModel.self) private var model

    /// The connectors are not in the device photo, so the back is the word
    /// list on its own. Same rows as the front, so nothing new to learn.
    var body: some View {
        VStack(spacing: 8) {
            ForEach(model.capabilities.inputs.filter { $0.face == .back }) { input in
                NavigationLink(value: input) {
                    PartRow(input: input)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(model.voiceOverSummary(of: input))
                .accessibilityHint("Opens this connector")
            }
        }
    }
}

