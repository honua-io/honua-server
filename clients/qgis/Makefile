PLUGIN_NAME := honua_qgis
DIST_DIR := dist
ZIP_PATH := $(DIST_DIR)/$(PLUGIN_NAME).zip

PYTHON ?= python3
PYTEST ?= $(PYTHON) -m pytest
RUFF ?= ruff

.PHONY: zip test lint e2e clean help

help:
	@echo "Targets:"
	@echo "  make zip   - build $(ZIP_PATH) for QGIS Plugin Manager / registry submission"
	@echo "  make test  - run unit tests (pure Python, no QGIS required)"
	@echo "  make lint  - run ruff over the plugin sources"
	@echo "  make e2e   - run docker-compose end-to-end test (requires Docker)"
	@echo "  make clean - remove dist/ artifacts"

zip:
	@mkdir -p $(DIST_DIR)
	$(PYTHON) scripts/build_zip.py --out $(ZIP_PATH)

test:
	$(PYTEST) -q tests

lint:
	$(RUFF) check $(PLUGIN_NAME) tests

e2e:
	docker compose -f docker/docker-compose.yml up --build --abort-on-container-exit --exit-code-from qgis-test

clean:
	rm -rf $(DIST_DIR)
	find . -type d -name __pycache__ -prune -exec rm -rf {} +
