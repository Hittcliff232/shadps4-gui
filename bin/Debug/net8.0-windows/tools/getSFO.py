import struct


def read_sfo(path):
    with open(path, "rb") as f:
        data = f.read()

    # Header
    magic, version, key_table_start, data_table_start, entry_count = struct.unpack_from(
        "<4sIIII", data, 0
    )

    if magic != b"\x00PSF":
        raise ValueError("Not a valid SFO file")

    entries = []
    offset = 20  # header size

    # Read index table
    for _ in range(entry_count):
        key_offset, fmt, length, max_length, data_offset = struct.unpack_from(
            "<HHIII", data, offset
        )
        entries.append((key_offset, fmt, length, max_length, data_offset))
        offset += 16

    result = {}

    for key_offset, fmt, length, max_length, data_offset in entries:
        # Read key
        key_start = key_table_start + key_offset
        key_end = data.find(b"\x00", key_start)
        key = data[key_start:key_end].decode("utf-8")

        # Read value
        value_start = data_table_start + data_offset

        if fmt == 0x0204:  # UTF-8 string
            raw = data[value_start:value_start + length]
            value = raw.rstrip(b"\x00").decode("utf-8")
        elif fmt == 0x0404:  # Integer
            value = struct.unpack_from("<I", data, value_start)[0]
        else:
            value = data[value_start:value_start + length]

        result[key] = value

    return result


if __name__ == "__main__":
    sfo_data = read_sfo("param.sfo")

    for k, v in sfo_data.items():
        print(f"{k}: {v}")