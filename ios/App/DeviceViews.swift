import SwiftUI
import QuadStickKit

/// The device picture is the navigation. Every physical part is a
/// NavigationLink with a full VoiceOver summary of its assignments.

struct DeviceFrontView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 24) {
            // LEDs sit above the tubes, same as the real front panel.
            LEDRowView()
            HStack(alignment: .center, spacing: 26) {
                tubeButton("left-tube")
                joystickAssembly
                tubeButton("right-tube")
                sideTube
            }
            lipSwitch
        }
        .padding(28)
        .frame(maxWidth: .infinity)
        .background(RoundedRectangle(cornerRadius: 28).fill(deviceBody))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Front of the QuadStick")
    }

    private var deviceBody: Color { Theme.deviceBody }

    private func tubeButton(_ id: String) -> some View {
        let input = model.capabilities.input(id)!
        return NavigationLink(value: input) {
            VStack(spacing: 8) {
                ZStack {
                    Circle().fill(Color(white: 0.28)).frame(width: 58, height: 58)
                    Circle().fill(.black).frame(width: 34, height: 34)
                }
                Text(shortName(input.name))
                    .font(.caption)
                    .foregroundStyle(.white)
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(model.voiceOverSummary(of: input))
        .accessibilityHint("Opens the editor for this input")
    }

    // The fourth sensor, the short mode select tube on the right side.
    private var sideTube: some View {
        let input = model.capabilities.input("side-tube")!
        return NavigationLink(value: input) {
            VStack(spacing: 8) {
                ZStack {
                    Circle().fill(Color(white: 0.28)).frame(width: 44, height: 44)
                    Circle().fill(.black).frame(width: 24, height: 24)
                }
                Text("Side\nTube")
                    .font(.caption)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.white)
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(model.voiceOverSummary(of: input))
        .accessibilityHint("Opens the editor for the side tube")
    }

    // The center tube passes through the joystick mouthpiece, so the two
    // tappable areas are drawn nested, like the hardware.
    private var joystickAssembly: some View {
        let joystick = model.capabilities.input("joystick")!
        let centerTube = model.capabilities.input("center-tube")!
        return VStack(spacing: 8) {
            ZStack {
                NavigationLink(value: joystick) {
                    Circle()
                        .strokeBorder(Color(white: 0.45), lineWidth: 26)
                        .frame(width: 116, height: 116)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(model.voiceOverSummary(of: joystick))
                .accessibilityHint("Opens the editor for the mouth joystick")

                NavigationLink(value: centerTube) {
                    ZStack {
                        Circle().fill(Color(white: 0.28)).frame(width: 58, height: 58)
                        Circle().fill(.black).frame(width: 34, height: 34)
                    }
                }
                .buttonStyle(.plain)
                .accessibilityLabel(model.voiceOverSummary(of: centerTube))
                .accessibilityHint("Opens the editor for the center tube")
            }
            Text("Center Tube · Joystick")
                .font(.caption)
                .foregroundStyle(.white)
        }
    }

    private var lipSwitch: some View {
        let input = model.capabilities.input("lip-switch")!
        return NavigationLink(value: input) {
            VStack(spacing: 6) {
                Capsule().fill(Color(white: 0.35)).frame(width: 110, height: 22)
                Text("Lip Switch").font(.caption).foregroundStyle(.white)
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(model.voiceOverSummary(of: input))
        .accessibilityHint("Opens the editor for the lip switch")
    }

    private func shortName(_ name: String) -> String {
        name.replacingOccurrences(of: " Tube", with: "\nTube")
    }
}

/// Mirrors the device's mode LEDs so the app and the hardware read as the
/// same state. Number labels keep it meaningful without colour.
// ponytail: lit LED = mode index; swap in the firmware's shift-bit map when
// the C# backend is wired.
struct LEDRowView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 6) {
            HStack(spacing: 18) {
                ForEach(0..<model.capabilities.ledCount, id: \.self) { i in
                    let lit = i == model.modeIndex
                    VStack(spacing: 3) {
                        Circle()
                            .fill(lit ? Color.green : Color(white: 0.08))
                            .overlay(Circle().strokeBorder(Color(white: 0.4), lineWidth: 1))
                            .frame(width: 16, height: 16)
                        Text("\(i + 1)")
                            .font(.caption2.bold())
                            .foregroundStyle(lit ? Color.green : Color(white: 0.55))
                    }
                }
            }
            Text("The lit light matches the real QuadStick")
                .font(.caption2)
                .foregroundStyle(Color(white: 0.6))
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Mode lights. Light \(model.modeIndex + 1) is on, matching mode \(model.modeIndex + 1) of \(model.profile.modes.count) on the real QuadStick.")
    }
}

struct DeviceBackView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 12) {
            backRow("jack-top")
            backRow("jack-middle")
            backRow("jack-bottom")
            backRow("usb-host")
        }
        .padding(20)
        .frame(maxWidth: .infinity)
        .background(RoundedRectangle(cornerRadius: 28).fill(Theme.deviceBody))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Back of the QuadStick")
    }

    private func backRow(_ id: String) -> some View {
        let input = model.capabilities.input(id)!
        let assigned = model.summary(of: input).filter { $0.assignment.output != nil }.count
        return NavigationLink(value: input) {
            HStack(spacing: 16) {
                portIcon(id)
                    .frame(width: 44, height: 44)
                VStack(alignment: .leading, spacing: 2) {
                    Text(input.name)
                        .font(.headline)
                        .foregroundStyle(.white)
                    if let detail = input.detail {
                        Text(detail)
                            .font(.caption)
                            .foregroundStyle(Color(white: 0.7))
                    }
                }
                Spacer()
                if assigned > 0 {
                    Text("\(assigned) assigned")
                        .font(.caption)
                        .foregroundStyle(Color(white: 0.7))
                }
                Image(systemName: "chevron.right")
                    .font(.caption)
                    .foregroundStyle(Color(white: 0.5))
            }
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 14).fill(Color(white: 0.22)))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(model.voiceOverSummary(of: input))
        .accessibilityHint("Opens the editor for this connector")
    }

    @ViewBuilder
    private func portIcon(_ id: String) -> some View {
        if id == "usb-host" {
            Image(systemName: "cable.connector")
                .font(.title2)
                .foregroundStyle(.white)
        } else {
            ZStack {
                Circle().fill(Color(white: 0.35))
                Circle().fill(.black).frame(width: 14, height: 14)
            }
        }
    }
}
