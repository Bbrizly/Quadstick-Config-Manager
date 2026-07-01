# make test | run | build | package | release | tag | clean

VERSION := 1.0.0
SLN := QuadStick.sln
APP := src/QuadStick.App/QuadStick.App.csproj
RIDS := win-x64 osx-arm64 osx-x64
DIST := dist

.PHONY: all test run build package release tag clean

all: test build

test:
	dotnet test $(SLN) --nologo -c Release

run:
	dotnet run --project $(APP)

build:
	dotnet build $(SLN) -c Release --nologo

package: build
	@mkdir -p $(DIST)
	@for rid in $(RIDS); do \
		echo "Publishing $$rid..."; \
		dotnet publish $(APP) -c Release -r $$rid \
			--self-contained true -p:PublishSingleFile=true \
			-p:Version=$(VERSION) -o $(DIST)/$$rid --nologo; \
		(cd $(DIST)/$$rid && zip -r ../QuadStickConfigManager-$$rid.zip .); \
		echo "  -> $(DIST)/QuadStickConfigManager-$$rid.zip"; \
	done

release: test package
	@echo "Release $(VERSION) ready in $(DIST)/"

tag:
	@if [ -n "$$(git status --porcelain)" ]; then \
		echo "Working tree not clean; commit first."; exit 1; \
	fi
	@if git rev-parse v$(VERSION) >/dev/null 2>&1; then \
		echo "Tag v$(VERSION) already exists."; exit 1; \
	fi
	git tag -a v$(VERSION) -m "QuadStick Config Manager v$(VERSION)"
	@echo "Tagged v$(VERSION). Push with: git push origin v$(VERSION)"

clean:
	rm -rf $(DIST)
	dotnet clean $(SLN) --nologo
