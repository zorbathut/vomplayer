# Build and install vomplayer as a flatpak.
#
#   make           validate requirements, then build the bundle (vomplayer.flatpak)
#   make install   build (if needed) and install it into the --user flatpak
#   make check      just validate the build/runtime requirements
#   make clean      remove the build dir, local repo, and bundle
#
# One-time prerequisites (runtimes/SDK) are documented in flatpak/README.md.

APP_ID   := io.github.zorbathut.vomplayer
MANIFEST := flatpak/$(APP_ID).yml
BUNDLE   := vomplayer.flatpak
REPO     := repo
BUILDDIR := build-flatpak

SOURCES := Vomplayer.csproj $(shell find src flatpak -type f)

# Required flatpak runtimes / SDK extensions (mirrors flatpak/README.md's
# one-time prerequisites; branches track the manifest's runtime-version 49 on
# the freedesktop 25.08 base).
FLATPAK_REFS := \
	org.gnome.Platform//49 \
	org.gnome.Sdk//49 \
	org.freedesktop.Sdk.Extension.dotnet10//25.08 \
	org.freedesktop.Platform.codecs-extra//25.08-extra

.PHONY: all install check clean

all: check $(BUNDLE)

# Build into a local OSTree repo, then export a single-file bundle from it.
$(BUNDLE): $(SOURCES)
	flatpak-builder --force-clean --repo=$(REPO) $(BUILDDIR) $(MANIFEST)
	flatpak build-bundle $(REPO) $@ $(APP_ID)

install: check $(BUNDLE)
	flatpak install --user -y --reinstall $(BUNDLE)
	@echo ">>> Installed. Run with: flatpak run $(APP_ID)"

# Fail early with an actionable message if a tool or runtime is missing,
# rather than partway through a multi-minute flatpak-builder run.
check:
	@command -v flatpak >/dev/null 2>&1 \
		|| { echo "error: 'flatpak' not found in PATH — install flatpak first."; exit 1; }
	@command -v flatpak-builder >/dev/null 2>&1 \
		|| { echo "error: 'flatpak-builder' not found in PATH — install the flatpak-builder package."; exit 1; }
	@missing=""; \
	for ref in $(FLATPAK_REFS); do \
		flatpak info "$$ref" >/dev/null 2>&1 || missing="$$missing $$ref"; \
	done; \
	if [ -n "$$missing" ]; then \
		echo "error: missing flatpak runtimes/extensions:$$missing"; \
		echo "install them with:"; \
		echo "    flatpak install --user flathub$$missing"; \
		exit 1; \
	fi; \
	echo ">>> requirements OK"

clean:
	rm -rf $(REPO) $(BUILDDIR) $(BUNDLE)
