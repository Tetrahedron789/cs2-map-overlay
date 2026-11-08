import { ModRegistrar } from "cs2/modding";
import { EditorFloatingButton, OverlayPanel, ToolbarButton, withEditorButton } from "mods/panel";

const editorToolbar = "game-ui/editor/components/toolbar/toolbar.tsx";
const editorToolTheme = "game-ui/editor/themes/editor-tool-button.module.scss";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("GameTopLeft", ToolbarButton);
    moduleRegistry.append("Game", OverlayPanel);
    moduleRegistry.append("Editor", OverlayPanel);

    // The editor has no slot for mod buttons: its button joins the row of native tools.
    const theme = moduleRegistry.registry.get(editorToolTheme)?.classes;
    if (theme && moduleRegistry.registry.get(editorToolbar)?.Toolbar) {
        moduleRegistry.extend(editorToolbar, "Toolbar", withEditorButton(theme));
    } else {
        moduleRegistry.append("Editor", EditorFloatingButton);
    }
}

export default register;
