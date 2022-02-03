# Flotilla backend

## Installation

```bash
pip install -e .[dev]
pip install fastapi[all]
pip install -i https://test.pypi.org/simple/ flotilla-openapi
```

## Running the API

Start the API by running

```bash
python src/flotilla/main.py
```

The API is available at `localhost:8000/docs`.

## Running the tests

The tests can be run with

```bash
pytest .
```