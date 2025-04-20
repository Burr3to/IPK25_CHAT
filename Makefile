PROJECT = IPK25_CHAT.csproj
OUTPUT_DIR = .
EXECUTABLE_NAME = ipk25chat-client
RUNTIME_IDENTIFIER = linux-x64
CONFIGURATION = Release

all: $(EXECUTABLE_NAME)

$(EXECUTABLE_NAME): $(PROJECT)
	dotnet publish $(PROJECT) -r $(RUNTIME_IDENTIFIER) -c $(CONFIGURATION) -o $(OUTPUT_DIR) /p:PublishSingleFile=true /p:SelfContained=true /p:AssemblyName=$(EXECUTABLE_NAME)

clean:
	dotnet clean $(PROJECT)
	rm -rf $(OUTPUT_DIR)/$(EXECUTABLE_NAME)
	rm -rf $(OUTPUT_DIR)/$(EXECUTABLE_NAME).deps.json
	rm -rf $(OUTPUT_DIR)/$(EXECUTABLE_NAME).runtimeconfig.json

build: $(PROJECT)
	dotnet build $(PROJECT) -c $(CONFIGURATION)