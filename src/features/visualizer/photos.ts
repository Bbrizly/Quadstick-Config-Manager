import fpsPhoto from "../../QuadStick.App/Assets/QuadStickFPS.png";
import originalPhoto from "../../QuadStick.App/Assets/QuadStickOriginal.png";
import singletonPhoto from "../../QuadStick.App/Assets/QuadStickSingleton.png";
import type { QsModel } from "./deviceDiagram";

const PHOTOS: Record<QsModel, string> = {
  fps: fpsPhoto,
  original: originalPhoto,
  singleton: singletonPhoto,
};

/** `MainWindow.DevicePhoto`: the front photo of each model. */
export function photoFor(model: QsModel): string {
  return PHOTOS[model];
}
