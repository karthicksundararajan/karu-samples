# Ecommerce application

Follow the adjacent Karu `docs/guide/README.md`. Keep configuration in YAML and algorithms in C# extensions. Application models must not reference UI or infrastructure. Use supported Karu data capabilities; never open database connections in extensions.

- Prepare source packages and runtime: `./tools/prepare.sh`
- Build: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Ecommerce.slnx`
- Unit tests: `export PATH="$HOME/.dotnet:$PATH" && dotnet test Ecommerce.slnx`
- Model/scenarios: `./tools/karu validate . && ./tools/karu test .`
- Migrations: `./tools/karu migrations check .`; generate changes with `migrations add`, never edit the snapshot.
- HTTP tests: `python3 tests/http_test.py`
- PostgreSQL HTTP tests: set a dedicated empty `KARU_ECOMMERCE_TEST_POSTGRES`, then `python3 tests/http_test.py --postgres`.

Run relevant tests before reporting completion. Add no dependencies without user authorization. Keep data, secrets, packages, compiler output and test artifacts out of Git.
