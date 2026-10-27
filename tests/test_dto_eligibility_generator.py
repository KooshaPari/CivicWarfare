from test_null_object_generator import run_generator_fixture


def test_dto_eligibility_compiles_and_serializes_real_generated_members(tmp_path):
    result = run_generator_fixture(tmp_path, 'LockedReasonIdGenerator', 'eligibility_generator_driver')
    assert 'eligibility generation and JSON checks passed' in result
