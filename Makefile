PROJECT = IPK25_CHAT.csproj
OUTPUT_DIR = publish

all: publish

publish: $(PROJECT)
	dotnet publish $(PROJECT) -r linux-x64 -c Release -o $(OUTPUT_DIR)

clean:
	dotnet clean $(PROJECT)
	rm -rf $(OUTPUT_DIR)

build: $(PROJECT)
	dotnet build $(PROJECT) -c Release