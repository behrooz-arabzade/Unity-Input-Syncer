UNITY := /Applications/Unity/Hub/Editor/6000.3.0f1/Unity.app/Contents/MacOS/Unity
PROJECT := $(CURDIR)
RESULTS_DIR := /tmp

.PHONY: test test-edit test-play

test: test-edit test-play

test-edit:
	$(UNITY) -runTests -batchmode -nographics \
		-projectPath "$(PROJECT)" \
		-testPlatform EditMode \
		-testResults $(RESULTS_DIR)/editmode-results.xml

test-play:
	$(UNITY) -runTests -batchmode -nographics \
		-projectPath "$(PROJECT)" \
		-testPlatform PlayMode \
		-testResults $(RESULTS_DIR)/playmode-results.xml
