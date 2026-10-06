#!/usr/bin/env python3
"""Merge the string tables under localization/*.tsv into the two .resx files.

One row per line, tab separated:  key <TAB> Simplified Chinese <TAB> English

Why a generated file rather than a hand-edited one: the tables are kept per area so that a single area can
be re-translated without touching the other twenty, and merging them by hand would make every such change a
diff against a thousand-line file. A duplicate key or a missing translation is an error here, not something
to notice later.

    python scripts/New-StringsResx.py localization src/FengSync/Localization/Strings.resx

Run it from the repository root. It rewrites the .resx in place; git shows the result as a normal diff. The
tables are the source of truth; never edit the .resx directly, because the next run overwrites it.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET


def unescape(value: str) -> str:
    return value.replace("\\n", "\n").replace("\\t", "\t").replace("\\r", "\r").replace("\\\\", "\\")


def escape(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
"""


def collect(folder: str):
    rows = {}
    for path in sorted(glob.glob(os.path.join(folder, "*.tsv"))):
        with open(path, encoding="utf-8") as handle:
            for number, line in enumerate(handle, 1):
                line = line.rstrip("\n")
                if not line.strip() or line.lstrip().startswith("#"):
                    continue
                parts = line.split("\t")
                if len(parts) != 3:
                    raise SystemExit(f"{path}:{number}: expected 3 tab separated columns, got {len(parts)}")
                key, chinese, english = (unescape(part) for part in parts)
                if key in rows:
                    raise SystemExit(f"duplicate key {key!r} in {path}:{number} (already in {rows[key][0]})")
                rows[key] = (f"{path}:{number}", chinese, english)
    if not rows:
        raise SystemExit(f"no .tsv files under {folder}")
    return rows


def write(path: str, rows, column: int, suffix: str):
    body = [HEADER]
    for key in sorted(rows):
        where, chinese, english = rows[key]
        value = (chinese, english)[column - 1]
        if not value:
            raise SystemExit(f"{key} ({where}) has no {'Chinese' if column == 1 else 'English'} text")
        body.append(f'  <data name="{escape(key)}" xml:space="preserve">\n    <value>{escape(value)}</value>\n  </data>\n')
    body.append("</root>\n")
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("".join(body))
    print(f"{path}: {len(rows)} strings ({suffix})")


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    folder, target = sys.argv[1], sys.argv[2]
    rows = collect(folder)
    write(target, rows, 1, "neutral")
    write(os.path.splitext(target)[0] + ".en.resx", rows, 2, "en")
    # Re-read both so a malformed document fails here rather than at the first lookup.
    for path in (target, os.path.splitext(target)[0] + ".en.resx"):
        ET.parse(path)
    print("both files parse")


if __name__ == "__main__":
    main()
