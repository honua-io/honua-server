"""Add Honua Server dialog.

Pops a ``QDialog`` with a name field, base URL field, API key field, and
a "Test connection" button. On accept, the connection is persisted via
``QSettings`` under the ``honua/connections/<name>`` group.

The pure form-validation logic lives in ``validate_form`` so it can be
unit-tested without instantiating the Qt dialog.
"""

from __future__ import annotations

from dataclasses import dataclass

from .auth import HonuaConnection, SETTINGS_GROUP
from .client import HonuaClient, HonuaClientError


@dataclass
class FormValidationResult:
    ok: bool
    error: str = ""


def validate_form(name: str, base_url: str, api_key: str) -> FormValidationResult:
    """Return the same error message the dialog would surface inline.

    Validation rules are intentionally minimal: name is required, base
    URL must be a syntactically valid http(s) URL with a host. API key
    may be empty (anonymous endpoints are valid). Anything stricter
    becomes a connectivity test, which is a separate explicit user
    action.
    """
    cleaned_name = (name or "").strip()
    cleaned_url = (base_url or "").strip()
    if not cleaned_name:
        return FormValidationResult(ok=False, error="Connection name is required.")
    if not cleaned_url:
        return FormValidationResult(ok=False, error="Honua server base URL is required.")
    try:
        HonuaConnection(name=cleaned_name, base_url=cleaned_url, api_key=api_key or "")
    except ValueError as exc:
        return FormValidationResult(ok=False, error=str(exc))
    return FormValidationResult(ok=True)


def test_connection(connection: HonuaConnection, *, client_factory=None) -> FormValidationResult:
    """Run the OGC API Features landing-page probe for the dialog button.

    A separate function so tests can inject a fake client. Returns the
    same result type as ``validate_form`` so the dialog can render both
    failure paths through one widget.
    """
    factory = client_factory or HonuaClient
    client = factory(connection)
    try:
        client.ping()
    except HonuaClientError as exc:
        return FormValidationResult(ok=False, error=str(exc))
    return FormValidationResult(ok=True)


# ---------------------------------------------------------------------------
# Qt dialog. Importing PyQt5 at module load time is fine inside QGIS, but
# in headless unit tests we exercise ``validate_form`` and ``test_connection``
# directly without ever constructing the dialog class. The Qt-dependent
# code is therefore guarded.
# ---------------------------------------------------------------------------

try:  # pragma: no cover - exercised only inside QGIS
    from qgis.PyQt.QtCore import QSettings, Qt
    from qgis.PyQt.QtWidgets import (
        QDialog,
        QDialogButtonBox,
        QFormLayout,
        QLabel,
        QLineEdit,
        QMessageBox,
        QPushButton,
        QVBoxLayout,
    )
except Exception:  # pragma: no cover - module-level fallback for tests
    QSettings = None  # type: ignore[assignment]
    Qt = None  # type: ignore[assignment]
    QDialog = object  # type: ignore[assignment,misc]
    QDialogButtonBox = None  # type: ignore[assignment]
    QFormLayout = None  # type: ignore[assignment]
    QLabel = None  # type: ignore[assignment]
    QLineEdit = None  # type: ignore[assignment]
    QMessageBox = None  # type: ignore[assignment]
    QPushButton = None  # type: ignore[assignment]
    QVBoxLayout = None  # type: ignore[assignment]


class AddHonuaServerDialog(QDialog):  # type: ignore[misc]
    """Add/edit dialog for a single ``HonuaConnection``."""

    def __init__(self, parent=None, *, existing: HonuaConnection | None = None):  # pragma: no cover - UI
        if QDialog is object:
            raise RuntimeError("PyQt5 is required to instantiate AddHonuaServerDialog")
        super().__init__(parent)
        self.setWindowTitle("Add Honua Server" if existing is None else f"Edit Honua Server — {existing.name}")
        self.setModal(True)
        self._existing = existing
        self._build_ui()
        if existing is not None:
            self.name_edit.setText(existing.name)
            self.url_edit.setText(existing.base_url)
            self.key_edit.setText(existing.api_key)

    # --- construction ---------------------------------------------------

    def _build_ui(self) -> None:  # pragma: no cover - UI
        layout = QVBoxLayout(self)
        form = QFormLayout()
        self.name_edit = QLineEdit(self)
        self.name_edit.setPlaceholderText("My Honua server")
        self.url_edit = QLineEdit(self)
        self.url_edit.setPlaceholderText("https://my.honua.io")
        self.key_edit = QLineEdit(self)
        self.key_edit.setEchoMode(QLineEdit.Password)
        self.key_edit.setPlaceholderText("API key (optional)")
        form.addRow("Name", self.name_edit)
        form.addRow("Base URL", self.url_edit)
        form.addRow("API key", self.key_edit)
        layout.addLayout(form)

        self.status_label = QLabel("", self)
        self.status_label.setWordWrap(True)
        layout.addWidget(self.status_label)

        self.test_button = QPushButton("Test connection", self)
        self.test_button.clicked.connect(self._on_test_clicked)
        layout.addWidget(self.test_button)

        button_box = QDialogButtonBox(
            QDialogButtonBox.Ok | QDialogButtonBox.Cancel,
            Qt.Horizontal,
            self,
        )
        button_box.accepted.connect(self._on_accept)
        button_box.rejected.connect(self.reject)
        layout.addWidget(button_box)

    # --- handlers -------------------------------------------------------

    def _current_connection(self) -> HonuaConnection | None:  # pragma: no cover - UI
        result = validate_form(self.name_edit.text(), self.url_edit.text(), self.key_edit.text())
        if not result.ok:
            self.status_label.setText(result.error)
            return None
        return HonuaConnection(
            name=self.name_edit.text().strip(),
            base_url=self.url_edit.text().strip(),
            api_key=self.key_edit.text(),
        )

    def _on_test_clicked(self) -> None:  # pragma: no cover - UI
        connection = self._current_connection()
        if connection is None:
            return
        self.status_label.setText("Testing connection…")
        result = test_connection(connection)
        if result.ok:
            self.status_label.setText("Connection succeeded.")
        else:
            self.status_label.setText(f"Connection failed: {result.error}")

    def _on_accept(self) -> None:  # pragma: no cover - UI
        connection = self._current_connection()
        if connection is None:
            return
        result = test_connection(connection)
        if not result.ok:
            answer = QMessageBox.question(
                self,
                "Save without test?",
                "Connectivity test failed:\n\n"
                f"{result.error}\n\nSave the connection anyway?",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.No,
            )
            if answer != QMessageBox.Yes:
                return
        save_connection(connection)
        self.accept()


# ---------------------------------------------------------------------------
# QSettings persistence helpers
# ---------------------------------------------------------------------------


def save_connection(connection: HonuaConnection) -> None:  # pragma: no cover - QSettings
    if QSettings is None:
        raise RuntimeError("QSettings is unavailable; this code runs inside QGIS")
    settings = QSettings()
    settings.beginGroup(f"{SETTINGS_GROUP}/{connection.name}")
    try:
        settings.setValue("base_url", connection.base_url)
        settings.setValue("api_key", connection.api_key)
    finally:
        settings.endGroup()


def load_connections() -> list[HonuaConnection]:  # pragma: no cover - QSettings
    if QSettings is None:
        return []
    settings = QSettings()
    settings.beginGroup(SETTINGS_GROUP)
    try:
        names = settings.childGroups()
        out: list[HonuaConnection] = []
        for name in names:
            settings.beginGroup(name)
            try:
                base_url = str(settings.value("base_url", "") or "")
                api_key = str(settings.value("api_key", "") or "")
            finally:
                settings.endGroup()
            if not base_url:
                continue
            try:
                out.append(HonuaConnection(name=name, base_url=base_url, api_key=api_key))
            except ValueError:
                continue
        return out
    finally:
        settings.endGroup()


def delete_connection(name: str) -> None:  # pragma: no cover - QSettings
    if QSettings is None:
        return
    settings = QSettings()
    settings.remove(f"{SETTINGS_GROUP}/{name}")
