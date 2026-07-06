# make test | run | build | package | release VERSION=x.y.z | clean

SLN := QuadStick.sln
APP := src/QuadStick.App/QuadStick.App.csproj
DIST := dist
# The Mac this is run on: builds the bundle you can actually launch to test.
HOSTRID := osx-$(shell uname -m | sed 's/x86_64/x64/')

.PHONY: all test run build package release clean

all: test build

test:
	dotnet test $(SLN) --nologo -c Release

run:
	dotnet run --project $(APP)

build:
	dotnet build $(SLN) -c Release --nologo

# Build the macOS .app locally to smoke-test the bundle before releasing.
# CI (.github/workflows/build.yml) builds the full Windows/macOS/Linux matrix;
# there is no need to cross-build platforms you can't run here.
package: build
	@mkdir -p $(DIST)
	dotnet publish $(APP) -c Release -r $(HOSTRID) --self-contained true -o $(DIST)/pub --nologo
	rm -f $(DIST)/pub/*.pdb
	scripts/make-macos-app.sh $(DIST)/pub 0.0.0-dev "$(DIST)/QuadStick Config Manager.app"
	@echo "Open '$(DIST)/QuadStick Config Manager.app' to test it."

# Ship a release: verify, tag, push. That is the whole process — pushing the
# tag triggers CI, which builds every download and publishes the release.
#   make release VERSION=1.2.3
release:
	@echo "$(VERSION)" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$$' \
		|| { echo "Set a semver VERSION, e.g. make release VERSION=1.2.3"; exit 1; }
	@[ -z "$$(git status --porcelain)" ] || { echo "Working tree not clean; commit first."; exit 1; }
	@git rev-parse "v$(VERSION)" >/dev/null 2>&1 && { echo "Tag v$(VERSION) already exists."; exit 1; } || true
	dotnet test $(SLN) --nologo -c Release
	git tag -a "v$(VERSION)" -m "QuadStick Config Manager v$(VERSION)"
	git push --follow-tags origin HEAD
	@echo "Pushed v$(VERSION). Building the release: https://github.com/Bbrizly/Quadstick-Config-Manager/actions"

clean:
	rm -rf $(DIST)
	dotnet clean $(SLN) --nologo
