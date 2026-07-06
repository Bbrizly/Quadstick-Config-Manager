# make test | run | build | package | release | tag | clean

VERSION := 1.0.0
SLN := QuadStick.sln
APP := src/QuadStick.App/QuadStick.App.csproj
RIDS := win-x64 osx-arm64 osx-x64 linux-x64
DIST := dist

.PHONY: all test run build package release tag clean

all: test build

test:
	dotnet test $(SLN) --nologo -c Release

run:
	dotnet run --project $(APP)

build:
	dotnet build $(SLN) -c Release --nologo

# Mirrors .github/workflows/build.yml: Windows/Linux ship one compressed
# single-file binary, macOS ships a .app bundle (shared make-macos-app.sh).
# macOS artifacts need ditto, so those RIDs only package on a Mac.
package: build
	@mkdir -p $(DIST)
	@for rid in $(RIDS); do \
		echo "Publishing $$rid..."; \
		case $$rid in \
			osx-*) sc="--self-contained true" ;; \
			*)     sc="--self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true" ;; \
		esac; \
		dotnet publish $(APP) -c Release -r $$rid $$sc -p:Version=$(VERSION) -o $(DIST)/pub-$$rid --nologo; \
		rm -f $(DIST)/pub-$$rid/*.pdb; \
		case $$rid in \
			win-x64)   out=QuadStickConfigManager-Windows-x64.zip; \
			           (cd $(DIST)/pub-$$rid && zip -qr ../$$out .) ;; \
			linux-x64) out=QuadStickConfigManager-Linux-x64.tar.gz; \
			           tar -C $(DIST)/pub-$$rid -czf $(DIST)/$$out . ;; \
			osx-arm64) out=QuadStickConfigManager-macOS-AppleSilicon.zip; \
			           scripts/make-macos-app.sh $(DIST)/pub-$$rid $(VERSION) "$(DIST)/QuadStick Config Manager.app"; \
			           (cd $(DIST) && ditto -c -k --keepParent "QuadStick Config Manager.app" $$out && rm -rf "QuadStick Config Manager.app") ;; \
			osx-x64)   out=QuadStickConfigManager-macOS-Intel.zip; \
			           scripts/make-macos-app.sh $(DIST)/pub-$$rid $(VERSION) "$(DIST)/QuadStick Config Manager.app"; \
			           (cd $(DIST) && ditto -c -k --keepParent "QuadStick Config Manager.app" $$out && rm -rf "QuadStick Config Manager.app") ;; \
		esac; \
		echo "  -> $(DIST)/$$out"; \
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
