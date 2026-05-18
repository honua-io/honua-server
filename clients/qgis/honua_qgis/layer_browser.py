"""Layer browser dock panel.

Tree of: ``Servers → <connection name> → [Vector / Raster] → <layer>``.
Double-clicking (or right-click → "Add to project") loads the layer onto
the canvas using the URI builders in ``layers.py``.

The view-model that converts a ``DiscoveryResult`` into a flat list of
displayable rows lives in ``flatten_for_view`` so it can be unit-tested
without Qt.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Literal

from .auth import (
    CollectionEntry,
    DiscoveryResult,
    HonuaConnection,
    WmsLayerEntry,
)
from .client import HonuaClient, HonuaClientError
from .layers import build_wfs_uri, build_wms_uri, display_label


LayerKind = Literal["vector", "raster"]


@dataclass
class BrowserRow:
    """One leaf row in the layer browser tree."""

    connection_name: str
    kind: LayerKind
    identifier: str
    label: str
    uri: str
    provider: str  # "WFS" or "wms"


def flatten_for_view(connection: HonuaConnection, discovery: DiscoveryResult) -> list[BrowserRow]:
    """Project a ``DiscoveryResult`` into UI rows in display order.

    Vector collections come first, then raster layers, so the user always
    sees feature data — usually the more interesting load — at the top
    of the tree.
    """
    rows: list[BrowserRow] = []
    for collection in discovery.collections:
        rows.append(_row_for_collection(connection, collection))
    for layer in discovery.wms_layers:
        rows.append(_row_for_wms_layer(connection, layer))
    return rows


def _row_for_collection(connection: HonuaConnection, collection: CollectionEntry) -> BrowserRow:
    return BrowserRow(
        connection_name=connection.name,
        kind="vector",
        identifier=collection.collection_id,
        label=display_label(kind="vector", title=collection.title, identifier=collection.collection_id),
        uri=build_wfs_uri(connection, collection),
        provider="WFS",
    )


def _row_for_wms_layer(connection: HonuaConnection, layer: WmsLayerEntry) -> BrowserRow:
    return BrowserRow(
        connection_name=connection.name,
        kind="raster",
        identifier=f"{layer.service_id}:{layer.layer_name}",
        label=display_label(
            kind="raster",
            title=layer.title,
            identifier=f"{layer.service_id}/{layer.layer_name}",
        ),
        uri=build_wms_uri(connection, layer),
        provider="wms",
    )


# ---------------------------------------------------------------------------
# Qt dock widget. PyQt5 imports are guarded so unit tests can exercise
# ``flatten_for_view`` without QGIS installed.
# ---------------------------------------------------------------------------

try:  # pragma: no cover - exercised inside QGIS
    from qgis.PyQt.QtCore import Qt
    from qgis.PyQt.QtWidgets import (
        QDockWidget,
        QHBoxLayout,
        QMessageBox,
        QPushButton,
        QTreeWidget,
        QTreeWidgetItem,
        QVBoxLayout,
        QWidget,
    )

    try:
        from qgis.core import QgsProject, QgsRasterLayer, QgsVectorLayer
    except Exception:  # QGIS not yet initialised
        QgsProject = None  # type: ignore[assignment]
        QgsRasterLayer = None  # type: ignore[assignment]
        QgsVectorLayer = None  # type: ignore[assignment]
except Exception:  # pragma: no cover - module-level fallback for tests
    Qt = None  # type: ignore[assignment]
    QDockWidget = object  # type: ignore[assignment,misc]
    QHBoxLayout = None  # type: ignore[assignment]
    QMessageBox = None  # type: ignore[assignment]
    QPushButton = None  # type: ignore[assignment]
    QTreeWidget = None  # type: ignore[assignment]
    QTreeWidgetItem = None  # type: ignore[assignment]
    QVBoxLayout = None  # type: ignore[assignment]
    QWidget = None  # type: ignore[assignment]
    QgsProject = None  # type: ignore[assignment]
    QgsRasterLayer = None  # type: ignore[assignment]
    QgsVectorLayer = None  # type: ignore[assignment]


class HonuaLayerBrowser(QDockWidget):  # type: ignore[misc]
    """Dock widget listing connections and their discovered layers."""

    def __init__(  # pragma: no cover - UI
        self,
        parent=None,
        *,
        client_factory: Callable[[HonuaConnection], HonuaClient] | None = None,
    ) -> None:
        if QDockWidget is object:
            raise RuntimeError("PyQt5 is required to instantiate HonuaLayerBrowser")
        super().__init__("Honua", parent)
        self.setObjectName("HonuaLayerBrowser")
        self._client_factory = client_factory or HonuaClient
        self._connections: list[HonuaConnection] = []
        self._build_ui()

    def _build_ui(self) -> None:  # pragma: no cover - UI
        container = QWidget(self)
        layout = QVBoxLayout(container)

        self.tree = QTreeWidget(container)
        self.tree.setHeaderLabels(["Layer", "Type"])
        self.tree.itemDoubleClicked.connect(self._on_item_double_clicked)
        layout.addWidget(self.tree)

        button_row = QHBoxLayout()
        self.refresh_button = QPushButton("Refresh", container)
        self.refresh_button.clicked.connect(self.refresh)
        button_row.addWidget(self.refresh_button)
        button_row.addStretch(1)
        layout.addLayout(button_row)

        self.setWidget(container)

    # ------------------------------------------------------------------
    # Public API used by the plugin shell
    # ------------------------------------------------------------------

    def set_connections(self, connections: list[HonuaConnection]) -> None:  # pragma: no cover - UI
        self._connections = list(connections)
        self.refresh()

    def refresh(self) -> None:  # pragma: no cover - UI
        self.tree.clear()
        for connection in self._connections:
            self._populate_connection_node(connection)

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _populate_connection_node(self, connection: HonuaConnection) -> None:  # pragma: no cover - UI
        root = QTreeWidgetItem([connection.name, "Server"])
        self.tree.addTopLevelItem(root)
        try:
            client = self._client_factory(connection)
            discovery = client.discover()
        except HonuaClientError as exc:
            error_node = QTreeWidgetItem([f"Discovery failed: {exc}", ""])
            root.addChild(error_node)
            root.setExpanded(True)
            return

        rows = flatten_for_view(connection, discovery)
        if not rows:
            empty = QTreeWidgetItem(["(no layers discovered)", ""])
            root.addChild(empty)
            root.setExpanded(True)
            return

        vector_group = QTreeWidgetItem(["Vector (OGC API Features)", ""])
        raster_group = QTreeWidgetItem(["Raster (WMS)", ""])
        for row in rows:
            child = QTreeWidgetItem([row.label, row.kind])
            child.setData(0, Qt.UserRole, row)
            if row.kind == "vector":
                vector_group.addChild(child)
            else:
                raster_group.addChild(child)
        if vector_group.childCount() > 0:
            root.addChild(vector_group)
            vector_group.setExpanded(True)
        if raster_group.childCount() > 0:
            root.addChild(raster_group)
            raster_group.setExpanded(True)
        root.setExpanded(True)

    def _on_item_double_clicked(self, item, _column: int) -> None:  # pragma: no cover - UI
        row = item.data(0, Qt.UserRole)
        if not isinstance(row, BrowserRow):
            return
        self._add_row_to_canvas(row)

    def _add_row_to_canvas(self, row: BrowserRow) -> None:  # pragma: no cover - UI
        if QgsProject is None or QgsVectorLayer is None or QgsRasterLayer is None:
            QMessageBox.warning(self, "Honua", "QGIS core is not initialised.")
            return
        if row.kind == "vector":
            layer = QgsVectorLayer(row.uri, row.label, row.provider)
        else:
            layer = QgsRasterLayer(row.uri, row.label, row.provider)
        if not layer.isValid():
            QMessageBox.warning(
                self,
                "Honua",
                f"Could not add layer '{row.label}'. Check the QGIS log for details.",
            )
            return
        QgsProject.instance().addMapLayer(layer)
