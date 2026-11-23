import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { SaveTemplateDialog } from "./SaveTemplateDialog";

describe("SaveTemplateDialog", () => {
  it("puts Cancel left and Save template right, as every dialog row does", () => {
    render(
      <I18nProvider initialPreference="en">
        <SaveTemplateDialog open suggested="Racing" busy={false} onSave={() => undefined} onClose={() => undefined} />
      </I18nProvider>,
    );
    const cancel = screen.getByRole("button", { name: "Cancel" });
    const save = screen.getByRole("button", { name: "Save template" });
    expect(cancel.compareDocumentPosition(save) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});
