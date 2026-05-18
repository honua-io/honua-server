"""Plugin shell — wires menu, toolbar, dialog, and dock panel.

QGIS calls ``initGui`` once on plugin load and ``unload`` when the plugin
is disabled or QGIS shuts down. We keep the shell free of business logic
so the dialog and dock panel can be tested in isolation.
"""

from __future__ import annotations

import os

from .auth import HonuaConnection


_PLUGIN_DIR = os.path.dirname(__file__)


try:  # pragma: no cover - exercised inside QGIS
    from qgis.PyQt.QtCore import Qt
    from qgis.PyQt.QtGui import QIcon
    from qgis.PyQt.QtWidgets import QAction, QMenu
except Exception:  # pragma: no cover
    Qt = None  # type: ignore[assignment]
    QIcon = None  # type: ignore[assignment]
    QAction = object  # type: ignore[assignment,misc]
    QMenu = None  # type: ignore[assignment]


class HonuaPlugin:
    """The QGIS-facing plugin object returned by ``classFactory``."""

    MENU_TITLE = "&Honua"
    TOOLBAR_NAME = "Honua"

    def __init__(self, iface) -> None:  # pragma: no cover - constructed by QGIS
        self.iface = iface
        self._actions: list[QAction] = []
        self._menu = None
        self._toolbar = None
        self._dock = None

    # ------------------------------------------------------------------
    # QGIS lifecycle
    # ------------------------------------------------------------------

    def initGui(self) -> None:  # pragma: no cover - UI wiring
        if QAction is object:
            raise RuntimeError("PyQt5 / PyQGIS is required to load the Honua plugin")
        icon = QIcon(os.path.join(_PLUGIN_DIR, "resources", "icon.svg"))

        self._toolbar = self.iface.addToolBar(self.TOOLBAR_NAME)
        self._toolbar.setObjectName("HonuaToolbar")

        add_action = QAction(icon, "Add Honua Server…", self.iface.mainWindow())
        add_action.setObjectName("HonuaAddServerAction")
        add_action.triggered.connect(self.show_add_server_dialog)
        self._register(add_action)

        browser_action = QAction(icon, "Show Layer Browser", self.iface.mainWindow())
        browser_action.setObjectName("HonuaBrowserAction")
        browser_action.triggered.connect(self.show_browser)
        self._register(browser_action)

    def unload(self) -> None:  # pragma: no cover - UI wiring
        for action in self._actions:
            self.iface.removePluginWebMenu(self.MENU_TITLE, action)
            if self._toolbar is not None:
                self._toolbar.removeAction(action)
        self._actions.clear()
        if self._toolbar is not None:
            self._toolbar.deleteLater()
            self._toolbar = None
        if self._dock is not None:
            self.iface.removeDockWidget(self._dock)
            self._dock.deleteLater()
            self._dock = None

    # ------------------------------------------------------------------
    # Action handlers (kept thin)
    # ------------------------------------------------------------------

    def show_add_server_dialog(self) -> None:  # pragma: no cover - UI
        from .dialog_add_server import AddHonuaServerDialog, load_connections

        dialog = AddHonuaServerDialog(self.iface.mainWindow())
        if dialog.exec_():
            self._refresh_browser(load_connections())

    def show_browser(self) -> None:  # pragma: no cover - UI
        from .dialog_add_server import load_connections

        self._refresh_browser(load_connections())
        if self._dock is not None:
            self._dock.show()
            self._dock.raise_()

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _register(self, action: QAction) -> None:  # pragma: no cover - UI
        self._actions.append(action)
        self.iface.addPluginToWebMenu(self.MENU_TITLE, action)
        if self._toolbar is not None:
            self._toolbar.addAction(action)

    def _refresh_browser(self, connections: list[HonuaConnection]) -> None:  # pragma: no cover - UI
        from .layer_browser import HonuaLayerBrowser

        if self._dock is None:
            self._dock = HonuaLayerBrowser(self.iface.mainWindow())
            self.iface.addDockWidget(Qt.RightDockWidgetArea, self._dock)
        self._dock.set_connections(connections)
